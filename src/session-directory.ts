import {
  normalizeSessionAlias,
  projectNameFromCwd,
  runtimeDefinition,
  runtimeDisplayName,
  sessionAddress,
  sessionAliasKey,
  sessionAliasValidationError,
  shortSessionId,
  statusLabel,
  type SessionRecord,
} from "./domain.js";
import {
  managedTerminalSessionId,
  ManagedTerminalRouter,
} from "./managed-terminal.js";
import { aliasCommandUsage, type AliasCommand } from "./message-command-parser.js";
import { OpenCodeManager } from "./opencode-manager.js";
import {
  captureLiveTrackedCodexProcessIds,
  type ClientProcessMetadata,
} from "./process-tracking.js";
import { SessionGroupCoordinator } from "./session-group-coordinator.js";
import { BridgeStore } from "./store.js";

export interface SessionAliasResult {
  ok: boolean;
  error?: string;
  session?: SessionRecord;
}

interface SessionDirectoryDependencies {
  store: BridgeStore;
  managedTerminals: ManagedTerminalRouter;
  opencode?: OpenCodeManager;
  sessionActiveMs: number;
  sessionGroups: SessionGroupCoordinator;
  liveClientProcessIds?: (
    clients: ClientProcessMetadata[],
  ) => ReadonlySet<number>;
  queuedPromptCount: (sessionId: string) => number;
  respond: (
    sourceMessageId: string,
    chatId: string,
    text: string,
  ) => Promise<string | undefined>;
}

export class SessionDirectory {
  constructor(private readonly dependencies: SessionDirectoryDependencies) {}

  listActive(): SessionRecord[] {
    const now = Date.now();
    const registrations = this.dependencies.managedTerminals.listOnline(now);
    const registrationById = new Map(
      registrations.map((registration) => [
        registration.terminalId,
        registration,
      ]),
    );
    const openSessions = this.dependencies.store.listOpenSessions();
    const trackedClients = openSessions.flatMap(
      (session): ClientProcessMetadata[] =>
        session.clientProcessId
          ? [{
              processId: session.clientProcessId,
              startedAt: session.clientProcessStartedAt,
              observedAt: session.lastSeenAt,
            }]
          : [],
    );
    const liveClientProcessIds = (
      this.dependencies.liveClientProcessIds ??
      captureLiveTrackedCodexProcessIds
    )(trackedClients);
    const sessions = openSessions.flatMap((session): SessionRecord[] => {
      if (runtimeDefinition(session.runtime).transport === "http_event_stream") {
        const instance = this.dependencies.opencode?.findActiveInstanceBySession(
          session.sessionId,
        );
        return instance ? [session] : [];
      }
      if (!this.dependencies.managedTerminals.isManaged(session)) {
        if (session.clientProcessId) {
          return liveClientProcessIds.has(session.clientProcessId)
            ? [session]
            : [];
        }
        const fallbackMs = Math.min(
          this.dependencies.sessionActiveMs,
          5 * 60 * 1_000,
        );
        return now - Date.parse(session.lastSeenAt) <= fallbackMs
          ? [session]
          : [];
      }
      const terminalId = session.managedTerminalId;
      const registration = terminalId
        ? registrationById.get(terminalId)
        : undefined;
      return registration
        ? [{
            ...session,
            lastSeenAt: new Date(registration.lastSeenAt).toISOString(),
          }]
        : [];
    });

    const representedTerminals = new Set(
      sessions
        .map((session) => session.managedTerminalId)
        .filter((terminalId): terminalId is string => Boolean(terminalId)),
    );
    for (const registration of registrations) {
      if (representedTerminals.has(registration.terminalId)) {
        continue;
      }
      sessions.push({
        sessionId: managedTerminalSessionId(registration.terminalId),
        shortId: shortSessionId(registration.terminalId),
        cwd: registration.cwd,
        projectName: projectNameFromCwd(registration.cwd),
        status: registration.ready ? "ready" : "starting",
        openedAt: new Date(registration.createdAt).toISOString(),
        lastSeenAt: new Date(registration.lastSeenAt).toISOString(),
        source: "managed_window",
        runtime: registration.runtime,
        managedTerminalId: registration.terminalId,
        managedTerminalElevated: registration.elevated,
      });
    }
    return sessions.sort(
      (left, right) =>
        Date.parse(right.lastSeenAt) - Date.parse(left.lastSeenAt),
    );
  }

  findByShortToken(token: string): SessionRecord[] {
    const normalized = token.replace(/[^a-zA-Z0-9]/g, "").toLowerCase();
    if (normalized.length < 4) {
      return [];
    }
    return this.listActive().filter((session) =>
      session.sessionId
        .replace(/[^a-zA-Z0-9]/g, "")
        .toLowerCase()
        .endsWith(normalized)
    );
  }

  findByAlias(alias: string): SessionRecord[] {
    const key = sessionAliasKey(alias);
    if (!key) {
      return [];
    }
    return this.listActive().filter(
      (session) => session.alias && sessionAliasKey(session.alias) === key,
    );
  }

  listAliasReserved(): SessionRecord[] {
    const sessions = new Map<string, SessionRecord>();
    for (const session of this.listActive()) {
      sessions.set(session.sessionId, session);
    }
    for (const session of this.dependencies.store.listAssistantManagedSessions()) {
      sessions.set(session.sessionId, session);
    }
    return [...sessions.values()];
  }

  async handleAliasUpdate(
    value: Record<string, unknown>,
  ): Promise<Record<string, unknown>> {
    const sessionId = typeof value.sessionId === "string"
      ? value.sessionId.trim()
      : "";
    const hasAlias = Object.prototype.hasOwnProperty.call(value, "alias");
    const aliasValue = value.alias;
    if (
      !sessionId ||
      !hasAlias ||
      (typeof aliasValue !== "string" && aliasValue !== null)
    ) {
      return { ok: false, error: "会话 ID 或别名参数不完整。" };
    }

    const session = this.listAliasReserved().find(
      (item) => item.sessionId === sessionId,
    );
    if (!session) {
      return {
        ok: false,
        error: "这个会话已不在活跃或历史列表中，请刷新后重试。",
      };
    }

    const result = await this.updateAlias(
      session,
      typeof aliasValue === "string" ? aliasValue : undefined,
    );
    return result.ok
      ? {
          ok: true,
          session: {
            sessionId: result.session?.sessionId,
            shortId: result.session?.shortId,
            alias: result.session?.alias ?? "",
          },
        }
      : { ok: false, error: result.error };
  }

  async updateAlias(
    session: SessionRecord,
    rawAlias: string | undefined,
  ): Promise<SessionAliasResult> {
    let persistentSession = this.dependencies.store.getSession(
      session.sessionId,
    );
    if (!persistentSession && session.source === "managed_window") {
      persistentSession = await this.dependencies.store.upsertSession({
        sessionId: session.sessionId,
        cwd: session.cwd,
        status: "ready",
        source: session.source,
        managedTerminalId: session.managedTerminalId,
        managedTerminalElevated: session.managedTerminalElevated,
      });
    }
    if (!persistentSession) {
      return { ok: false, error: "会话不存在或已经失效。" };
    }

    if (rawAlias === undefined || !rawAlias.trim()) {
      const updated = await this.dependencies.store.setSessionAlias(
        persistentSession.sessionId,
        undefined,
      );
      if (updated?.feishuChatId) {
        await this.renameGroup(updated);
      }
      return updated
        ? { ok: true, session: updated }
        : { ok: false, error: "会话不存在或已经失效。" };
    }

    const validationError = sessionAliasValidationError(rawAlias);
    if (validationError) {
      return { ok: false, error: validationError };
    }
    const alias = normalizeSessionAlias(rawAlias);
    const update = await this.dependencies.store.setUniqueSessionAlias(
      persistentSession.sessionId,
      alias,
      this.listAliasReserved().map((item) => item.sessionId),
    );
    if (update.conflict) {
      return {
        ok: false,
        error: `别名 @${alias} 已被会话 ${update.conflict.projectName} #${update.conflict.shortId} 使用。`,
      };
    }

    const updated = update.session;
    if (updated?.feishuChatId) {
      await this.renameGroup(updated);
    }
    return updated
      ? { ok: true, session: updated }
      : { ok: false, error: "会话不存在或已经失效。" };
  }

  async handleAliasCommand(
    command: AliasCommand,
    messageId: string,
    chatId: string,
  ): Promise<void> {
    if (!command.targetKind || !command.target) {
      await this.dependencies.respond(messageId, chatId, this.formatAliasList());
      return;
    }

    const matches = command.targetKind === "short"
      ? this.findByShortToken(command.target)
      : this.findByAlias(command.target);
    const address = command.targetKind === "short"
      ? `#${command.target}`
      : `@${command.target}`;
    if (matches.length !== 1) {
      await this.dependencies.respond(
        messageId,
        chatId,
        matches.length === 0
          ? `没有找到 ${address} 对应的活跃会话。发送“会话”查看列表。`
          : `${address} 匹配到多个会话，请换用完整短 ID。`,
      );
      return;
    }

    const session = matches[0]!;
    if (command.alias === undefined) {
      await this.dependencies.respond(
        messageId,
        chatId,
        session.alias
          ? `会话 ${session.projectName} #${session.shortId} 的别名是 @${session.alias}。`
          : `会话 ${session.projectName} #${session.shortId} 尚未设置别名。`,
      );
      return;
    }

    const clear = ["清除", "删除", "clear", "none"].includes(
      command.alias.trim().toLowerCase(),
    );
    const result = await this.updateAlias(
      session,
      clear ? undefined : command.alias,
    );
    if (!result.ok || !result.session) {
      await this.dependencies.respond(
        messageId,
        chatId,
        result.error ?? "设置别名失败。",
      );
      return;
    }

    await this.dependencies.respond(
      messageId,
      chatId,
      result.session.alias
        ? `已将 ${result.session.projectName} #${result.session.shortId} 的别名设为 @${result.session.alias}。以后可发送“@${result.session.alias} 回复内容”。`
        : `已清除 ${result.session.projectName} #${result.session.shortId} 的别名。`,
    );
  }

  activeDefinition(): string {
    const fallbackMs = Math.min(
      this.dependencies.sessionActiveMs,
      5 * 60 * 1_000,
    );
    return `活跃定义：助手打开的 Codex / Claude Code 窗口从打开到关闭始终算活跃；每个 opencode 窗口只登记当前对话，历史会话不算活跃；外部会话会跟踪真实 CLI 进程，进程关闭后自动移除。无法取得进程信息时仅临时保留 ${formatDuration(fallbackMs)}。`;
  }

  formatSessionList(): string {
    const sessions = this.listActive();
    if (sessions.length === 0) {
      return `当前没有活跃助手会话。\n${this.activeDefinition()}`;
    }
    const lines = sessions.slice(0, 20).map((session, index) => {
      const kind = runtimeDisplayName(session.runtime);
      const runtime = runtimeDefinition(session.runtime);
      const mode = runtime.transport === "http_event_stream"
        ? ` · ${kind} 窗口`
        : session.managedTerminalId
          ? session.managedTerminalElevated
            ? ` · ${kind} 管理员同步`
            : ` · ${kind} 窗口同步`
          : ` · ${kind} 外部会话（仅通知）`;
      const address = session.alias
        ? `@${session.alias}  (#${session.shortId})`
        : sessionAddress(session);
      const queued = this.dependencies.queuedPromptCount(session.sessionId);
      return `${index + 1}. ${address}  ${session.projectName}  · ${statusLabel(session.status)}${mode}${queued > 0 ? ` · 排队 ${queued}` : ""}`;
    });
    return `当前活跃会话：\n${lines.join("\n")}\n\n回复：@别名 内容；排队：排队 @别名 内容；文件回传：发文件 @别名 要求\n${this.activeDefinition()}`;
  }

  formatAliasList(): string {
    const sessions = this.listActive();
    if (sessions.length === 0) {
      return `当前没有可设置别名的活跃会话。\n\n${aliasCommandUsage()}`;
    }
    const lines = sessions.slice(0, 20).map(
      (session, index) =>
        `${index + 1}. ${session.alias ? `@${session.alias}` : "（未设置）"} · #${session.shortId} · ${session.projectName}`,
    );
    return `当前会话别名：\n${lines.join("\n")}\n\n${aliasCommandUsage()}`;
  }

  private async renameGroup(session: SessionRecord): Promise<void> {
    await this.dependencies.sessionGroups.rename(session).catch((error) => {
      console.warn("[feishu] Could not rename session group:", error);
    });
  }
}

function formatDuration(milliseconds: number): string {
  const hours = milliseconds / (60 * 60 * 1_000);
  if (Number.isInteger(hours)) {
    return `${hours} 小时`;
  }
  const minutes = Math.max(1, Math.round(milliseconds / (60 * 1_000)));
  return `${minutes} 分钟`;
}
