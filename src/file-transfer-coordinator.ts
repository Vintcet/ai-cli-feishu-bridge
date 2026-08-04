import {
  LocalAttachmentStore,
  type IncomingAttachment,
  type SavedAttachment,
} from "./attachments.js";
import type { MessageRouteKind, SessionRecord } from "./domain.js";
import { truncate } from "./domain.js";
import { FeishuGateway } from "./feishu.js";
import { validateBridgeFile } from "./file-transfer.js";

interface StagedAttachments {
  createdAt: number;
  files: SavedAttachment[];
}

interface FileReturnRequest {
  chatId: string;
  remainingStops: number;
  expiresAt: number;
}

interface FileTransferCoordinatorDependencies {
  feishu: FeishuGateway;
  uploadsDirectory: string;
  inboundFileMaxBytes: number;
  inboundAttachmentMaxCount: number;
  uploadMaxFiles: number;
  uploadMaxBytes: number;
  uploadTtlMs: number;
  outboundFileMaxBytes: number;
  addRoute: (
    messageId: string,
    sessionId: string,
    chatId: string,
    kind: MessageRouteKind,
  ) => Promise<void>;
}

export class FileTransferCoordinator {
  private readonly attachmentStore: LocalAttachmentStore;
  private readonly stagedAttachments = new Map<string, StagedAttachments>();
  private readonly returnRequests = new Map<string, FileReturnRequest[]>();

  constructor(
    private readonly dependencies: FileTransferCoordinatorDependencies,
  ) {
    this.attachmentStore = new LocalAttachmentStore(
      dependencies.uploadsDirectory,
      dependencies.inboundFileMaxBytes,
      dependencies.inboundAttachmentMaxCount,
      dependencies.uploadTtlMs,
      dependencies.uploadMaxFiles,
      dependencies.uploadMaxBytes,
    );
  }

  attachmentKey(openId: string, chatId: string): string {
    return `${openId}\u0000${chatId}`;
  }

  async downloadAndStage(
    key: string,
    messageId: string,
    attachments: IncomingAttachment[],
  ): Promise<SavedAttachment[]> {
    const files = await this.attachmentStore.download(
      this.dependencies.feishu,
      messageId,
      attachments,
    );
    this.stage(key, files);
    return files;
  }

  peek(key: string): SavedAttachment[] {
    this.pruneStaged();
    return this.stagedAttachments.get(key)?.files ?? [];
  }

  take(key: string): SavedAttachment[] {
    this.pruneStaged();
    const staged = this.stagedAttachments.get(key);
    this.stagedAttachments.delete(key);
    return staged?.files ?? [];
  }

  registerReturnRequest(
    sessionId: string,
    chatId: string,
    remainingStops: number,
  ): void {
    const now = Date.now();
    const requests = (this.returnRequests.get(sessionId) ?? []).filter(
      (request) => request.expiresAt > now,
    );
    requests.push({
      chatId,
      remainingStops: Math.max(0, remainingStops),
      expiresAt: now + 2 * 60 * 60 * 1_000,
    });
    this.returnRequests.set(sessionId, requests);
  }

  advanceReturnRequests(sessionId: string): FileReturnRequest | undefined {
    const now = Date.now();
    const requests = (this.returnRequests.get(sessionId) ?? []).filter(
      (request) => request.expiresAt > now,
    );
    const eligibleIndex = requests.findIndex(
      (request) => request.remainingStops === 0,
    );
    const eligible = eligibleIndex >= 0
      ? requests.splice(eligibleIndex, 1)[0]
      : undefined;
    for (const request of requests) {
      if (request.remainingStops > 0) {
        request.remainingStops -= 1;
      }
    }
    if (requests.length > 0) {
      this.returnRequests.set(sessionId, requests);
    } else {
      this.returnRequests.delete(sessionId);
    }
    return eligible;
  }

  removeSession(sessionId: string): void {
    this.returnRequests.delete(sessionId);
  }

  rekeySession(previousSessionId: string, sessionId: string): void {
    const requests = this.returnRequests.get(previousSessionId);
    if (!requests) {
      return;
    }
    this.returnRequests.delete(previousSessionId);
    this.returnRequests.set(sessionId, requests);
  }

  async sendRequestedFiles(
    session: SessionRecord,
    chatId: string,
    candidates: string[],
  ): Promise<void> {
    const errors: string[] = [];
    let sentCount = 0;
    for (const candidate of candidates.slice(0, 3)) {
      try {
        const file = await validateBridgeFile(
          candidate,
          session.cwd,
          this.dependencies.outboundFileMaxBytes,
        );
        const messageId = await this.dependencies.feishu.sendLocalFile(
          chatId,
          file.path,
        );
        await this.dependencies.addRoute(
          messageId,
          session.sessionId,
          chatId,
          "stop",
        );
        sentCount += 1;
      } catch (error) {
        const detail = error instanceof Error ? error.message : String(error);
        errors.push(`${candidate}：${detail}`);
      }
    }
    if (errors.length > 0) {
      await this.dependencies.feishu.sendText(
        chatId,
        `文件回传结果：成功 ${sentCount} 个，失败 ${errors.length} 个。\n${errors
          .map((error) => `- ${truncate(error, 400)}`)
          .join("\n")}`,
      );
    }
  }

  private stage(key: string, files: SavedAttachment[]): void {
    this.pruneStaged();
    const current = this.stagedAttachments.get(key)?.files ?? [];
    const limit = Math.max(
      1,
      this.dependencies.inboundAttachmentMaxCount * 2,
    );
    this.stagedAttachments.set(key, {
      createdAt: Date.now(),
      files: [...current, ...files].slice(-limit),
    });
  }

  private pruneStaged(): void {
    const cutoff = Date.now() - this.dependencies.uploadTtlMs;
    for (const [key, staged] of this.stagedAttachments) {
      if (staged.createdAt < cutoff) {
        this.stagedAttachments.delete(key);
      }
    }
  }
}
