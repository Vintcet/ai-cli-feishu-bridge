import {
  runtimeDisplayName,
  runtimeGroupPrefix,
  sessionLabel,
  truncate,
  type Binding,
  type SessionRecord,
} from "./domain.js";
import { FeishuGateway } from "./feishu.js";
import { BridgeStore } from "./store.js";

export interface NotificationRecipient {
  chatId: string;
  binding?: Binding;
}

export class SessionGroupCoordinator {
  private readonly creates = new Map<
    string,
    Promise<SessionRecord | undefined>
  >();

  constructor(
    private readonly store: BridgeStore,
    private readonly feishu: FeishuGateway,
    private readonly inactiveMs = 7 * 24 * 60 * 60 * 1_000,
  ) {}

  async initialize(): Promise<void> {
    if (!this.store.getOwnerOpenId()) {
      return;
    }
    const now = Date.now();
    const sessions = this.store
      .listOpenSessions()
      .filter(
        (session) =>
          session.managedByAssistant === true &&
          (Boolean(session.feishuChatId) ||
            now - sessionGroupActivityTime(session) < this.inactiveMs),
      )
      .sort(
        (left, right) =>
          Date.parse(left.openedAt) - Date.parse(right.openedAt) ||
          left.sessionId.localeCompare(right.sessionId),
      );
    for (const session of sessions) {
      const numbered = await this.store.ensureSessionFeishuChatOrdinal(
        session.sessionId,
      );
      if (numbered?.feishuChatId) {
        await this.rename(numbered);
      } else {
        await this.ensure(session.sessionId);
      }
    }
  }

  async cleanup(
    now = Date.now(),
  ): Promise<{ deleted: number; failed: number }> {
    let deleted = 0;
    let failed = 0;
    for (const session of this.store.listSessionsWithFeishuGroups()) {
      const chatId = session.feishuChatId;
      if (!chatId || now - sessionGroupActivityTime(session) < this.inactiveMs) {
        continue;
      }
      try {
        await this.feishu.deleteSessionGroup(chatId);
        await this.store.clearSessionFeishuChat(session.sessionId, chatId);
        deleted += 1;
        console.log(
          `[feishu] Dissolved inactive session group ${chatId} for #${session.shortId}.`,
        );
      } catch (error) {
        failed += 1;
        console.warn(
          `[feishu] Could not dissolve inactive group ${chatId} for #${session.shortId}:`,
          error,
        );
      }
    }
    return { deleted, failed };
  }

  async retry(sessionId: string): Promise<Record<string, unknown>> {
    const session = this.store.getSession(sessionId);
    if (!session || session.managedByAssistant !== true) {
      return { ok: false, error: "这个会话不存在，或不是由助手创建的。" };
    }
    if (session.feishuChatId) {
      return {
        ok: true,
        alreadyConnected: true,
        chatId: session.feishuChatId,
        chatName: session.feishuChatName ?? "",
      };
    }
    await this.store.setSessionFeishuChatError(sessionId, undefined);
    const updated = await this.ensure(sessionId, true);
    return updated?.feishuChatId
      ? {
          ok: true,
          alreadyConnected: false,
          chatId: updated.feishuChatId,
          chatName: updated.feishuChatName ?? "",
        }
      : {
          ok: false,
          error: updated?.feishuChatError || "飞书群创建失败，请检查应用权限后重试。",
        };
  }

  async notificationRecipients(
    session: SessionRecord,
  ): Promise<NotificationRecipient[]> {
    if (session.managedByAssistant === true) {
      const ensured = await this.ensure(session.sessionId);
      if (ensured?.feishuChatId) {
        return [{ chatId: ensured.feishuChatId }];
      }
    }
    return this.uniqueChatBindings().map((binding) => ({
      chatId: binding.chatId,
      binding,
    }));
  }

  async ensure(
    sessionId: string,
    forceRetry = false,
  ): Promise<SessionRecord | undefined> {
    const session = this.store.getSession(sessionId);
    if (!session || session.managedByAssistant !== true) {
      return session;
    }
    if (session.feishuChatId) {
      return await this.store.ensureSessionFeishuChatOrdinal(sessionId) ?? session;
    }
    // Persisted failures are retried only from the desktop action. This keeps
    // ordinary notifications from repeatedly calling the create-chat API
    // while permissions are still missing.
    if (session.feishuChatError && !forceRetry) {
      return session;
    }
    const ownerOpenId = this.store.getOwnerOpenId();
    if (!ownerOpenId) {
      return session;
    }
    const pending = this.creates.get(sessionId);
    if (pending) {
      return await pending;
    }
    const operation = this.create(session, ownerOpenId);
    this.creates.set(sessionId, operation);
    try {
      return await operation;
    } finally {
      if (this.creates.get(sessionId) === operation) {
        this.creates.delete(sessionId);
      }
    }
  }

  async rename(session: SessionRecord): Promise<void> {
    if (!session.feishuChatId) {
      return;
    }
    const numbered = await this.store.ensureSessionFeishuChatOrdinal(
      session.sessionId,
    ) ?? session;
    const name = this.groupName(numbered);
    if (numbered.feishuChatName === name) {
      return;
    }
    await this.feishu.updateSessionGroupName(numbered.feishuChatId!, name);
    await this.store.setSessionFeishuChat(numbered.sessionId, {
      chatId: numbered.feishuChatId!,
      chatName: name,
      createdAt: numbered.feishuChatCreatedAt,
    });
  }

  private uniqueChatBindings(): Binding[] {
    const byChat = new Map<string, Binding>();
    for (const binding of this.store.listBindings()) {
      byChat.set(binding.chatId, binding);
    }
    return [...byChat.values()];
  }

  private async create(
    session: SessionRecord,
    ownerOpenId: string,
  ): Promise<SessionRecord | undefined> {
    const numbered = await this.store.ensureSessionFeishuChatOrdinal(
      session.sessionId,
    ) ?? session;
    const name = this.groupName(numbered);
    const kind = runtimeDisplayName(numbered.runtime);
    try {
      const group = await this.feishu.createSessionGroup(
        ownerOpenId,
        name,
        `${kind} 会话 ${numbered.shortId} · ${numbered.cwd}`,
      );
      const updated = await this.store.setSessionFeishuChat(numbered.sessionId, {
        chatId: group.chatId,
        chatName: group.name,
      });
      if (updated) {
        try {
          await this.feishu.sendText(
            group.chatId,
            `已连接到 ${sessionLabel(updated)}。以后这个群里的消息都会发送到对应 ${kind} 窗口。`,
          );
        } catch (error) {
          console.warn(
            "[feishu] Session group created, but welcome message failed:",
            error,
          );
        }
      }
      console.log(
        `[feishu] Created session group ${group.chatId} for #${session.shortId}.`,
      );
      return updated ?? session;
    } catch (error) {
      const detail = truncate(
        error instanceof Error ? error.message : String(error),
        500,
      );
      await this.store.setSessionFeishuChatError(session.sessionId, detail);
      console.warn(
        `[feishu] Could not create session group for #${session.shortId}: ${detail}`,
      );
      return this.store.getSession(session.sessionId) ?? session;
    }
  }

  private groupName(session: SessionRecord): string {
    const prefix = runtimeGroupPrefix(session.runtime);
    const base = session.alias || session.projectName || session.shortId;
    const suffix = !session.alias && (session.feishuChatOrdinal ?? 1) > 1
      ? `（${session.feishuChatOrdinal}）`
      : "";
    return `${prefix}${base.slice(0, Math.max(0, 60 - prefix.length - suffix.length))}${suffix}`;
  }
}

function sessionGroupActivityTime(session: SessionRecord): number {
  return Math.max(
    parseTimestamp(session.lastSeenAt),
    parseTimestamp(session.feishuChatCreatedAt),
  );
}

function parseTimestamp(value: string | undefined): number {
  const parsed = value ? Date.parse(value) : Number.NaN;
  return Number.isFinite(parsed) ? parsed : 0;
}
