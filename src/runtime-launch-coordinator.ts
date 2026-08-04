import { randomUUID } from "node:crypto";
import path from "node:path";

import {
  runtimeDefinition,
  runtimeDisplayName,
  truncate,
  type RuntimeName,
  type SessionRecord,
} from "./domain.js";
import { ManagedTerminalRouter } from "./managed-terminal.js";
import {
  type NewRuntimeCommand,
  prepareProjectDirectory,
  projectDirectoryNameValidationError,
} from "./message-command-parser.js";
import { OpenCodeManager } from "./opencode-manager.js";
import { BridgeStore } from "./store.js";

export interface PendingRuntimeLaunchPrompt {
  prompt: string;
  sourceMessageId: string;
  chatId: string;
  requestFileReturn: boolean;
  queueRequested: boolean;
}

type RuntimeLaunchStatus = "pending" | "claimed" | "launched";
type RuntimeLaunchKind = "resume" | "new";

interface RuntimeLaunchRequest {
  requestId: string;
  kind: RuntimeLaunchKind;
  sessionId?: string;
  runtime: RuntimeName;
  cwd: string;
  projectName?: string;
  sourceMessageId?: string;
  chatId?: string;
  elevated: boolean;
  createdAt: string;
  status: RuntimeLaunchStatus;
  timer: NodeJS.Timeout;
}

interface RuntimeLaunchCoordinatorDependencies {
  store: BridgeStore;
  managedTerminals: ManagedTerminalRouter;
  opencode?: OpenCodeManager;
  timeoutMs?: number;
  respond: (
    sourceMessageId: string,
    chatId: string,
    text: string,
  ) => Promise<string | undefined>;
  resume: (
    session: SessionRecord,
    item: PendingRuntimeLaunchPrompt,
  ) => Promise<void>;
}

export class RuntimeLaunchCoordinator {
  private readonly queuedPrompts = new Map<
    string,
    PendingRuntimeLaunchPrompt[]
  >();
  private readonly requests = new Map<string, RuntimeLaunchRequest>();
  private readonly requestIdsBySession = new Map<string, string>();

  constructor(
    private readonly dependencies: RuntimeLaunchCoordinatorDependencies,
  ) {}

  get pendingCount(): number {
    return this.requests.size;
  }

  get queuedPromptCount(): number {
    return [...this.queuedPrompts.values()].reduce(
      (total, queue) => total + queue.length,
      0,
    );
  }

  dispose(): void {
    for (const request of this.requests.values()) {
      clearTimeout(request.timer);
    }
    this.requests.clear();
    this.requestIdsBySession.clear();
    this.queuedPrompts.clear();
  }

  claim(): Record<string, unknown> {
    const request = [...this.requests.values()]
      .filter((item) => item.status === "pending")
      .sort(
        (left, right) =>
          left.createdAt.localeCompare(right.createdAt) ||
          left.requestId.localeCompare(right.requestId),
      )[0];
    if (!request) {
      return { ok: true };
    }
    request.status = "claimed";
    return {
      ok: true,
      request: {
        requestId: request.requestId,
        kind: request.kind,
        sessionId: request.sessionId,
        runtime: request.runtime,
        cwd: request.cwd,
        projectName: request.projectName,
        elevated: request.elevated,
        createdAt: request.createdAt,
      },
    };
  }

  async complete(
    value: Record<string, unknown>,
  ): Promise<Record<string, unknown>> {
    const requestId = typeof value.requestId === "string"
      ? value.requestId.trim()
      : "";
    const success = value.success;
    if (!requestId || typeof success !== "boolean") {
      return { ok: false, error: "自动恢复结果参数不完整。" };
    }
    const request = this.requests.get(requestId);
    if (!request) {
      return { ok: true, alreadyResolved: true };
    }
    if (success) {
      if (request.kind === "new") {
        this.clearRequest(request);
        return { ok: true, kind: request.kind };
      }
      request.status = "launched";
      return { ok: true, sessionId: request.sessionId };
    }
    const detail = typeof value.error === "string" && value.error.trim()
      ? truncate(value.error.trim(), 500)
      : "桌面助手未能启动对应窗口。";
    await this.fail(request, detail);
    return { ok: true, sessionId: request.sessionId };
  }

  async handleNewCommand(
    command: NewRuntimeCommand,
    sourceMessageId: string,
    chatId: string,
  ): Promise<void> {
    const validationError = projectDirectoryNameValidationError(
      command.projectName,
    );
    if (validationError) {
      await this.dependencies.respond(
        sourceMessageId,
        chatId,
        `项目名不正确：${validationError}`,
      );
      return;
    }
    const workspaceRoot = this.dependencies.store.getSettings().workspaceRoot;
    if (!workspaceRoot) {
      await this.dependencies.respond(
        sourceMessageId,
        chatId,
        "尚未设置默认工作区。请先在电脑端“设置”中选择默认工作区。",
      );
      return;
    }
    let prepared: { cwd: string; created: boolean };
    try {
      prepared = await prepareProjectDirectory(
        workspaceRoot,
        command.projectName,
      );
    } catch (error) {
      await this.dependencies.respond(
        sourceMessageId,
        chatId,
        `项目目录准备失败：${error instanceof Error ? error.message : String(error)}`,
      );
      return;
    }
    await this.queueNew(
      command.runtime,
      prepared.cwd,
      command.projectName,
      sourceMessageId,
      chatId,
      prepared.created,
    );
  }

  isAvailable(session: SessionRecord): boolean {
    if (session.status === "ended") {
      return false;
    }
    if (runtimeDefinition(session.runtime).transport === "http_event_stream") {
      return Boolean(
        this.dependencies.opencode?.findActiveInstanceBySession(
          session.sessionId,
        ),
      );
    }
    return this.dependencies.managedTerminals.isReady(session);
  }

  async queueResume(
    session: SessionRecord,
    item: PendingRuntimeLaunchPrompt,
  ): Promise<void> {
    const queue = this.queuedPrompts.get(session.sessionId) ?? [];
    queue.push(item);
    this.queuedPrompts.set(session.sessionId, queue);

    const existingRequestId = this.requestIdsBySession.get(session.sessionId);
    const existingRequest = existingRequestId
      ? this.requests.get(existingRequestId)
      : undefined;
    if (existingRequest) {
      await this.dependencies.respond(
        item.sourceMessageId,
        item.chatId,
        `${runtimeDisplayName(session.runtime)} 会话正在自动恢复；这条消息会在窗口就绪后发送。`,
      );
      return;
    }

    const requestId = randomUUID();
    const timer = this.timeout(requestId, () =>
      "等待桌面助手自动打开窗口超时。请确认面板正在运行，然后在群里重试。"
    );
    const request: RuntimeLaunchRequest = {
      requestId,
      kind: "resume",
      sessionId: session.sessionId,
      runtime: session.runtime ?? "codex",
      cwd: session.cwd,
      elevated: session.managedTerminalElevated === true,
      createdAt: new Date().toISOString(),
      status: this.isStarting(session) ? "launched" : "pending",
      timer,
    };
    this.requests.set(requestId, request);
    this.requestIdsBySession.set(session.sessionId, requestId);
    await this.dependencies.respond(
      item.sourceMessageId,
      item.chatId,
      request.status === "pending"
        ? `${runtimeDisplayName(session.runtime)} 窗口已关闭，正在请求电脑端自动恢复；这条消息会在窗口就绪后发送。`
        : `${runtimeDisplayName(session.runtime)} 窗口正在启动；这条消息会在窗口就绪后发送。`,
    );
  }

  async drain(sessionId: string): Promise<void> {
    const session = this.dependencies.store.getSession(sessionId);
    if (!session || !this.isAvailable(session)) {
      return;
    }
    const requestId = this.requestIdsBySession.get(sessionId);
    const request = requestId ? this.requests.get(requestId) : undefined;
    if (request) {
      this.clearRequest(request);
    }
    const queue = this.queuedPrompts.get(sessionId);
    if (!queue?.length) {
      this.queuedPrompts.delete(sessionId);
      return;
    }
    this.queuedPrompts.delete(sessionId);
    for (let index = 0; index < queue.length; index += 1) {
      const item = queue[index]!;
      const current = this.dependencies.store.getSession(sessionId) ?? session;
      await this.dependencies.resume(current, {
        ...item,
        queueRequested: item.queueRequested || index > 0,
      });
    }
  }

  private async queueNew(
    runtime: RuntimeName,
    cwd: string,
    projectName: string,
    sourceMessageId: string,
    chatId: string,
    directoryCreated: boolean,
  ): Promise<void> {
    const requestId = randomUUID();
    const timer = this.timeout(
      requestId,
      () => "等待桌面助手打开窗口超时。请确认面板正在运行，然后重试。",
    );
    const request: RuntimeLaunchRequest = {
      requestId,
      kind: "new",
      runtime,
      cwd,
      projectName,
      sourceMessageId,
      chatId,
      elevated: false,
      createdAt: new Date().toISOString(),
      status: "pending",
      timer,
    };
    this.requests.set(requestId, request);
    await this.dependencies.respond(
      sourceMessageId,
      chatId,
      `${directoryCreated ? "已创建" : "已找到"}项目“${projectName}”：${cwd}\n正在请求电脑端启动 ${runtimeDisplayName(runtime)}；会话登记后会自动创建对应飞书群。`,
    );
  }

  private isStarting(session: SessionRecord): boolean {
    if (runtimeDefinition(session.runtime).transport === "http_event_stream") {
      return this.dependencies.opencode?.hasPendingSession(session.sessionId) ===
        true;
    }
    const normalizedCwd = normalizeRuntimeCwd(session.cwd);
    return this.dependencies.managedTerminals.listOnline().some(
      (registration) =>
        normalizeRuntimeCwd(registration.cwd) === normalizedCwd &&
        registration.runtime === (session.runtime ?? "codex") &&
        (!registration.sessionId ||
          registration.sessionId === session.sessionId),
    );
  }

  private timeout(
    requestId: string,
    detail: () => string,
  ): NodeJS.Timeout {
    const timer = setTimeout(() => {
      const request = this.requests.get(requestId);
      if (request) {
        void this.fail(request, detail());
      }
    }, this.dependencies.timeoutMs ?? 2 * 60 * 1_000);
    timer.unref?.();
    return timer;
  }

  private async fail(
    request: RuntimeLaunchRequest,
    detail: string,
  ): Promise<void> {
    this.clearRequest(request);
    if (request.kind === "new") {
      if (request.sourceMessageId && request.chatId) {
        await this.dependencies.respond(
          request.sourceMessageId,
          request.chatId,
          `${runtimeDisplayName(request.runtime)} 未启动：${detail}`,
        );
      }
      return;
    }
    const sessionId = request.sessionId;
    if (!sessionId) {
      return;
    }
    const queue = this.queuedPrompts.get(sessionId) ?? [];
    this.queuedPrompts.delete(sessionId);
    const session = this.dependencies.store.getSession(sessionId);
    for (const item of queue) {
      await this.dependencies.respond(
        item.sourceMessageId,
        item.chatId,
        `${runtimeDisplayName(session?.runtime ?? request.runtime)} 未接收：${detail}`,
      );
    }
  }

  private clearRequest(request: RuntimeLaunchRequest): void {
    clearTimeout(request.timer);
    this.requests.delete(request.requestId);
    if (
      request.sessionId &&
      this.requestIdsBySession.get(request.sessionId) === request.requestId
    ) {
      this.requestIdsBySession.delete(request.sessionId);
    }
  }
}

function normalizeRuntimeCwd(cwd: string): string {
  const resolved = path.resolve(cwd);
  return process.platform === "win32" ? resolved.toLowerCase() : resolved;
}
