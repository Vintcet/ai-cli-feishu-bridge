import { randomBytes } from "node:crypto";
import { mkdir } from "node:fs/promises";
import path from "node:path";

import type {
  ApprovalRecord,
  ApprovalResolution,
  ApprovalStore,
  BridgeSettings,
  Binding,
  BindingStore,
  MessageRoute,
  RouteStore,
  RuntimeName,
  SessionRecord,
  SessionStatus,
  SessionStore,
} from "./domain.js";
import {
  projectNameFromCwd,
  sessionAliasKey,
  shortSessionId,
  stringifyModel,
} from "./domain.js";
import { JsonFilePersistence } from "./json-file-persistence.js";
import {
  isApprovalStoreValue,
  isBindingStoreValue,
  isPlainRecord,
  isRouteStoreValue,
  isSessionStoreValue,
} from "./store-schema.js";

const emptyBindings = (): BindingStore => ({ users: {} });
const emptySessions = (): SessionStore => ({ sessions: {} });
const emptyRoutes = (): RouteStore => ({ messages: {}, processedInbound: {} });
const emptyApprovals = (): ApprovalStore => ({ requests: {} });
const defaultSettings = (workspaceRoot = ""): BridgeSettings => ({
  workspaceRoot,
  notifyActivity: false,
  notifyUserPrompts: false,
  autoRetryErrors: false,
  retryMaxAttempts: 3,
  retryIntervalSeconds: 5,
  retryJitterSeconds: 3,
  autoApprove: false,
  notifyAutoApprovals: false,
});

function integerInRange(
  value: unknown,
  minimum: number,
  maximum: number,
  fallback: number,
): number {
  return typeof value === "number" &&
      Number.isSafeInteger(value) &&
      value >= minimum &&
      value <= maximum
    ? value
    : fallback;
}

export interface BridgeStoreOptions {
  /** 首次运行时使用的默认项目工作区根目录。 */
  defaultWorkspaceRoot?: string;
  /** 高频会话写入合并落盘的时间窗口（毫秒）。 */
  persistDebounceMs?: number;
  /** 已结束会话的保留时长（毫秒），超过后从 sessions.json 中清理。 */
  endedSessionRetentionMs?: number;
}

const defaultPersistDebounceMs = 500;
const defaultEndedSessionRetentionMs = 90 * 24 * 60 * 60 * 1000;

export class BridgeStore {
  private readonly bindingFile: string;
  private readonly sessionFile: string;
  private readonly routeFile: string;
  private readonly approvalFile: string;
  private readonly controlTokenFile: string;
  private readonly settingsFile: string;

  private bindings: BindingStore = emptyBindings();
  private sessions: SessionStore = emptySessions();
  private routes: RouteStore = emptyRoutes();
  private approvals: ApprovalStore = emptyApprovals();
  private settings: BridgeSettings = defaultSettings();
  private mutationQueue: Promise<void> = Promise.resolve();
  private readonly approvalClaims = new Set<string>();

  private readonly endedSessionRetentionMs: number;
  private readonly defaultWorkspaceRoot: string;
  private readonly persistence: JsonFilePersistence;

  constructor(
    private readonly dataDirectory: string,
    options: BridgeStoreOptions = {},
  ) {
    this.bindingFile = path.join(dataDirectory, "bindings.json");
    this.sessionFile = path.join(dataDirectory, "sessions.json");
    this.routeFile = path.join(dataDirectory, "message-routes.json");
    this.approvalFile = path.join(dataDirectory, "approvals.json");
    this.controlTokenFile = path.join(dataDirectory, "control-token.json");
    this.settingsFile = path.join(dataDirectory, "settings.json");
    this.endedSessionRetentionMs =
      options.endedSessionRetentionMs ?? defaultEndedSessionRetentionMs;
    this.defaultWorkspaceRoot = options.defaultWorkspaceRoot
      ? path.resolve(options.defaultWorkspaceRoot)
      : "";
    this.persistence = new JsonFilePersistence({
      dataDirectory,
      persistDebounceMs:
        options.persistDebounceMs ?? defaultPersistDebounceMs,
      stateForFile: (filePath) => this.inMemoryState(filePath),
      runMutation: (operation) => this.mutate(operation),
      awaitMutations: () => this.mutationQueue,
      onSafetyFlush: () => this.performSafetyMaintenance(),
    });
  }

  async init(): Promise<void> {
    await mkdir(this.dataDirectory, { recursive: true });
    const [bindings, sessions, routes, approvals, settings] = await Promise.all([
      this.readJson(this.bindingFile, emptyBindings(), isBindingStoreValue),
      this.readJson(this.sessionFile, emptySessions(), isSessionStoreValue),
      this.readJson(this.routeFile, emptyRoutes(), isRouteStoreValue),
      this.readJson(this.approvalFile, emptyApprovals(), isApprovalStoreValue),
      this.readJson(
        this.settingsFile,
        defaultSettings(this.defaultWorkspaceRoot),
        isPlainRecord,
      ),
    ]);
    this.bindings = bindings;
    this.sessions = sessions;
    const rawRoutes = routes as Partial<RouteStore>;
    let routesChanged = !rawRoutes.messages || !rawRoutes.processedInbound;
    this.routes = {
      messages: rawRoutes.messages ?? {},
      processedInbound: rawRoutes.processedInbound ?? {},
    };
    this.approvals = approvals;
    const loadedSettings = settings && typeof settings === "object"
      ? settings as Partial<BridgeSettings>
      : {};
    this.settings = {
      workspaceRoot:
        typeof loadedSettings.workspaceRoot === "string" &&
          loadedSettings.workspaceRoot.trim()
          ? path.resolve(loadedSettings.workspaceRoot.trim())
          : this.defaultWorkspaceRoot,
      notifyActivity: loadedSettings.notifyActivity === true,
      notifyUserPrompts: loadedSettings.notifyUserPrompts === true,
      autoRetryErrors: loadedSettings.autoRetryErrors === true,
      retryMaxAttempts: integerInRange(
        loadedSettings.retryMaxAttempts,
        1,
        20,
        3,
      ),
      retryIntervalSeconds: integerInRange(
        loadedSettings.retryIntervalSeconds,
        1,
        600,
        5,
      ),
      retryJitterSeconds: integerInRange(
        loadedSettings.retryJitterSeconds,
        0,
        120,
        3,
      ),
      autoApprove: loadedSettings.autoApprove === true,
      notifyAutoApprovals: loadedSettings.notifyAutoApprovals === true,
    };

    const now = Date.now();
    let bindingsChanged = false;
    let sessionsChanged = false;
    const existingBindings = Object.values(this.bindings.users).sort(
      (left, right) => Date.parse(left.boundAt) - Date.parse(right.boundAt),
    );
    if (!this.bindings.ownerOpenId && existingBindings[0]) {
      this.bindings.ownerOpenId = existingBindings[0].openId;
      bindingsChanged = true;
    }
    if (this.bindings.ownerOpenId) {
      for (const openId of Object.keys(this.bindings.users)) {
        if (openId !== this.bindings.ownerOpenId) {
          delete this.bindings.users[openId];
          bindingsChanged = true;
        }
      }
      if (this.bindings.pairingCode) {
        delete this.bindings.pairingCode;
        bindingsChanged = true;
      }
    } else if (!this.bindings.pairingCode) {
      this.bindings.pairingCode = createPairingCode();
      bindingsChanged = true;
    }
    for (const session of Object.values(this.sessions.sessions)) {
      if (!session.openedAt) {
        session.openedAt = session.lastSeenAt || new Date(now).toISOString();
        sessionsChanged = true;
      }
      if (session.managedTerminalId && session.managedByAssistant !== true) {
        session.managedByAssistant = true;
        sessionsChanged = true;
      } else if (
        !session.managedTerminalId &&
        session.clientProcessId &&
        session.managedByAssistant === true
      ) {
        session.managedByAssistant = false;
        sessionsChanged = true;
      }
      if (
        session.historyHiddenAt &&
        session.managedByAssistant === true &&
        session.status !== "ended"
      ) {
        delete session.historyHiddenAt;
        sessionsChanged = true;
      }
    }
    let approvalsChanged = false;
    for (const approval of Object.values(this.approvals.requests)) {
      if (approval.status === "pending") {
        approval.status = "orphaned";
        approval.resolution = "local";
        approval.resolvedAt = new Date(now).toISOString();
        const session = this.sessions.sessions[approval.sessionId];
        if (session?.status === "pending_approval") {
          session.status = "local_approval";
          session.lastSeenAt = new Date(now).toISOString();
        }
        approvalsChanged = true;
      }
    }
    const pruneChanges = this.pruneInMemory(now);
    routesChanged ||= pruneChanges.routesChanged;
    approvalsChanged ||= pruneChanges.approvalsChanged;
    sessionsChanged ||= this.pruneEndedSessions(now);
    await Promise.all([
      bindingsChanged ? this.writeJson(this.bindingFile, this.bindings) : Promise.resolve(),
      routesChanged ? this.writeJson(this.routeFile, this.routes) : Promise.resolve(),
      approvalsChanged ? this.writeJson(this.approvalFile, this.approvals) : Promise.resolve(),
      approvalsChanged || sessionsChanged
        ? this.writeJson(this.sessionFile, this.sessions)
        : Promise.resolve(),
    ]);
    this.startSafetyFlush();
  }

  getSettings(): BridgeSettings {
    return { ...this.settings };
  }

  async updateSettings(value: Partial<BridgeSettings>): Promise<BridgeSettings> {
    return this.mutate(async () => {
      this.settings = {
        workspaceRoot:
          typeof value.workspaceRoot === "string" && value.workspaceRoot.trim()
            ? path.resolve(value.workspaceRoot.trim())
            : this.settings.workspaceRoot,
        notifyActivity:
          typeof value.notifyActivity === "boolean"
            ? value.notifyActivity
            : this.settings.notifyActivity,
        notifyUserPrompts:
          typeof value.notifyUserPrompts === "boolean"
            ? value.notifyUserPrompts
            : this.settings.notifyUserPrompts,
        autoRetryErrors:
          typeof value.autoRetryErrors === "boolean"
            ? value.autoRetryErrors
            : this.settings.autoRetryErrors,
        retryMaxAttempts: integerInRange(
          value.retryMaxAttempts,
          1,
          20,
          this.settings.retryMaxAttempts,
        ),
        retryIntervalSeconds: integerInRange(
          value.retryIntervalSeconds,
          1,
          600,
          this.settings.retryIntervalSeconds,
        ),
        retryJitterSeconds: integerInRange(
          value.retryJitterSeconds,
          0,
          120,
          this.settings.retryJitterSeconds,
        ),
        autoApprove:
          typeof value.autoApprove === "boolean"
            ? value.autoApprove
            : this.settings.autoApprove,
        notifyAutoApprovals:
          typeof value.notifyAutoApprovals === "boolean"
            ? value.notifyAutoApprovals
            : this.settings.notifyAutoApprovals,
      };
      await this.writeJson(this.settingsFile, this.settings);
      return { ...this.settings };
    });
  }

  listBindings(): Binding[] {
    const ownerOpenId = this.bindings.ownerOpenId;
    const binding = ownerOpenId ? this.bindings.users[ownerOpenId] : undefined;
    return binding ? [binding] : [];
  }

  isBound(openId: string): boolean {
    return this.bindings.ownerOpenId === openId && Boolean(this.bindings.users[openId]);
  }

  getOwnerOpenId(): string | undefined {
    return this.bindings.ownerOpenId;
  }

  getPairingCode(): string | undefined {
    return this.bindings.ownerOpenId ? undefined : this.bindings.pairingCode;
  }

  async getOrCreateControlToken(): Promise<string> {
    const existing = await this.readJson<{ token?: unknown }>(
      this.controlTokenFile,
      {},
      isPlainRecord,
    );
    if (
      typeof existing.token === "string" &&
      /^[a-f0-9]{64}$/i.test(existing.token)
    ) {
      return existing.token;
    }

    const token = randomBytes(32).toString("hex");
    await this.writeJson(this.controlTokenFile, { token });
    return token;
  }

  async bindOwner(
    binding: Binding,
    pairingCode: string | undefined,
  ): Promise<"bound" | "rebound" | "invalid_code" | "owner_mismatch"> {
    return this.mutate(async () => {
      const ownerOpenId = this.bindings.ownerOpenId;
      if (ownerOpenId && ownerOpenId !== binding.openId) {
        return "owner_mismatch";
      }
      if (!ownerOpenId) {
        const expected = this.bindings.pairingCode?.toUpperCase();
        if (!expected || pairingCode?.trim().toUpperCase() !== expected) {
          return "invalid_code";
        }
        this.bindings.ownerOpenId = binding.openId;
        delete this.bindings.pairingCode;
      }
      this.bindings.users = { [binding.openId]: binding };
      await this.writeJson(this.bindingFile, this.bindings);
      return ownerOpenId ? "rebound" : "bound";
    });
  }

  async removeBinding(openId: string): Promise<boolean> {
    return this.mutate(async () => {
      if (!this.bindings.users[openId]) {
        return false;
      }
      delete this.bindings.users[openId];
      await this.writeJson(this.bindingFile, this.bindings);
      return true;
    });
  }

  getSession(sessionId: string): SessionRecord | undefined {
    return this.sessions.sessions[sessionId];
  }

  findSessionByManagedTerminalId(terminalId: string): SessionRecord | undefined {
    return Object.values(this.sessions.sessions).find(
      (session) =>
        session.managedTerminalId === terminalId && session.status !== "ended",
    );
  }

  listOpenSessions(): SessionRecord[] {
    return Object.values(this.sessions.sessions)
      .filter((session) => session.status !== "ended")
      .sort((left, right) => Date.parse(right.lastSeenAt) - Date.parse(left.lastSeenAt));
  }

  listActiveSessions(activeMs: number, now = Date.now()): SessionRecord[] {
    return Object.values(this.sessions.sessions)
      .filter(
        (session) =>
          session.status !== "ended" && now - Date.parse(session.lastSeenAt) <= activeMs,
      )
      .sort((left, right) => Date.parse(right.lastSeenAt) - Date.parse(left.lastSeenAt));
  }

  listAssistantManagedSessions(): SessionRecord[] {
    return Object.values(this.sessions.sessions)
      .filter(
        (session) =>
          session.managedByAssistant === true &&
          !session.historyHiddenAt &&
          !session.sessionId.startsWith("managed-terminal-"),
      )
      .sort(
        (left, right) =>
          Date.parse(right.endedAt ?? right.lastSeenAt) -
          Date.parse(left.endedAt ?? left.lastSeenAt),
      );
  }

  listSessionsWithFeishuGroups(): SessionRecord[] {
    return Object.values(this.sessions.sessions)
      .filter(
        (session) =>
          session.managedByAssistant === true &&
          Boolean(session.feishuChatId),
      )
      .sort((left, right) => Date.parse(left.lastSeenAt) - Date.parse(right.lastSeenAt));
  }

  listPendingTurnNotifications(): SessionRecord[] {
    return Object.values(this.sessions.sessions).filter(
      (session) =>
        session.lastNotificationStatus === "pending" &&
        Boolean(session.lastNotificationTurnId),
    );
  }

  async upsertSession(input: {
    sessionId: string;
    alias?: string;
    cwd: string;
    model?: string;
    turnId?: string;
    status: SessionStatus;
    assistantMessage?: string | null;
    error?: string;
    source?: string;
    runtime?: RuntimeName | null;
    clientProcessId?: number | null;
    clientProcessStartedAt?: string | null;
    managedTerminalId?: string | null;
    managedTerminalElevated?: boolean | null;
    managedByAssistant?: boolean;
    transcriptPath?: string | null;
    openedAt?: string;
    feishuChatId?: string;
    feishuChatName?: string;
    feishuChatCreatedAt?: string;
    feishuChatError?: string | null;
    feishuChatErrorAt?: string | null;
  }): Promise<SessionRecord> {
    return this.mutate(async () => {
      const current = this.sessions.sessions[input.sessionId];
      const now = new Date().toISOString();
      const hasManagedTerminalId = Object.prototype.hasOwnProperty.call(
        input,
        "managedTerminalId",
      );
      const hasClientProcessId = Object.prototype.hasOwnProperty.call(
        input,
        "clientProcessId",
      );
      const hasClientProcessStartedAt = Object.prototype.hasOwnProperty.call(
        input,
        "clientProcessStartedAt",
      );
      const hasManagedTerminalElevated = Object.prototype.hasOwnProperty.call(
        input,
        "managedTerminalElevated",
      );
      const hasTranscriptPath = Object.prototype.hasOwnProperty.call(
        input,
        "transcriptPath",
      );
      const hasFeishuChatError = Object.prototype.hasOwnProperty.call(
        input,
        "feishuChatError",
      );
      const hasFeishuChatErrorAt = Object.prototype.hasOwnProperty.call(
        input,
        "feishuChatErrorAt",
      );
      const managedByAssistant =
        input.managedByAssistant ??
        current?.managedByAssistant ??
        Boolean(input.managedTerminalId);
      const next: SessionRecord = {
        sessionId: input.sessionId,
        shortId: shortSessionId(input.sessionId),
        alias: input.alias ?? current?.alias,
        cwd: input.cwd,
        projectName: projectNameFromCwd(input.cwd),
        model: stringifyModel(input.model ?? current?.model),
        status: input.status,
        openedAt:
          input.openedAt ??
          (current?.status === "ended" && input.status !== "ended"
            ? now
            : current?.openedAt ?? now),
        lastSeenAt: now,
        lastTurnId: input.turnId ?? current?.lastTurnId,
        lastAssistantMessage:
          input.assistantMessage === undefined
            ? current?.lastAssistantMessage
            : input.assistantMessage ?? undefined,
        lastNotificationTurnId: current?.lastNotificationTurnId,
        lastNotificationStatus: current?.lastNotificationStatus,
        pendingNotificationKind: current?.pendingNotificationKind,
        pendingNotificationMessage: current?.pendingNotificationMessage,
        lastError: input.error ?? (input.status === "error" ? current?.lastError : undefined),
        source: input.source ?? current?.source,
        runtime: input.runtime ?? current?.runtime,
        endedAt: input.status === "ended" ? now : undefined,
        clientProcessId: hasClientProcessId
          ? input.clientProcessId ?? undefined
          : current?.clientProcessId,
        clientProcessStartedAt: hasClientProcessStartedAt
          ? input.clientProcessStartedAt ?? undefined
          : current?.clientProcessStartedAt,
        managedTerminalId: hasManagedTerminalId
          ? input.managedTerminalId ?? undefined
          : current?.managedTerminalId,
        managedTerminalElevated: hasManagedTerminalElevated
          ? input.managedTerminalElevated ?? undefined
          : current?.managedTerminalElevated,
        managedByAssistant,
        transcriptPath: hasTranscriptPath
          ? input.transcriptPath ?? undefined
          : current?.transcriptPath,
        historyHiddenAt:
          input.status !== "ended" && managedByAssistant
            ? undefined
            : current?.historyHiddenAt,
        feishuChatId: input.feishuChatId ?? current?.feishuChatId,
        feishuChatName: input.feishuChatName ?? current?.feishuChatName,
        feishuChatCreatedAt:
          input.feishuChatCreatedAt ?? current?.feishuChatCreatedAt,
        feishuChatError: hasFeishuChatError
          ? input.feishuChatError ?? undefined
          : current?.feishuChatError,
        feishuChatErrorAt: hasFeishuChatErrorAt
          ? input.feishuChatErrorAt ?? undefined
          : current?.feishuChatErrorAt,
      };
      this.sessions.sessions[input.sessionId] = next;
      this.schedulePersist(this.sessionFile);
      return next;
    });
  }

  async setSessionAlias(
    sessionId: string,
    alias: string | undefined,
  ): Promise<SessionRecord | undefined> {
    return this.mutate(async () => {
      const session = this.sessions.sessions[sessionId];
      if (!session) {
        return undefined;
      }
      session.alias = alias;
      await this.writeJson(this.sessionFile, this.sessions);
      return session;
    });
  }

  findSessionByFeishuChatId(chatId: string): SessionRecord | undefined {
    return Object.values(this.sessions.sessions).find(
      (session) => session.feishuChatId === chatId,
    );
  }

  async setSessionFeishuChat(
    sessionId: string,
    value: {
      chatId: string;
      chatName: string;
      createdAt?: string;
    },
  ): Promise<SessionRecord | undefined> {
    return this.mutate(async () => {
      const session = this.sessions.sessions[sessionId];
      if (!session) {
        return undefined;
      }
      session.feishuChatId = value.chatId;
      session.feishuChatName = value.chatName;
      session.feishuChatCreatedAt = value.createdAt ?? new Date().toISOString();
      delete session.feishuChatError;
      delete session.feishuChatErrorAt;
      await this.writeJson(this.sessionFile, this.sessions);
      return session;
    });
  }

  async setSessionFeishuChatError(
    sessionId: string,
    error: string | undefined,
  ): Promise<SessionRecord | undefined> {
    return this.mutate(async () => {
      const session = this.sessions.sessions[sessionId];
      if (!session) {
        return undefined;
      }
      if (error) {
        session.feishuChatError = error;
        session.feishuChatErrorAt = new Date().toISOString();
      } else {
        delete session.feishuChatError;
        delete session.feishuChatErrorAt;
      }
      await this.writeJson(this.sessionFile, this.sessions);
      return session;
    });
  }

  async clearSessionFeishuChat(
    sessionId: string,
    expectedChatId?: string,
  ): Promise<SessionRecord | undefined> {
    return this.mutate(async () => {
      const session = this.sessions.sessions[sessionId];
      if (
        !session ||
        (expectedChatId !== undefined && session.feishuChatId !== expectedChatId)
      ) {
        return session;
      }
      delete session.feishuChatId;
      delete session.feishuChatName;
      delete session.feishuChatCreatedAt;
      delete session.feishuChatError;
      delete session.feishuChatErrorAt;
      await this.writeJson(this.sessionFile, this.sessions);
      return session;
    });
  }

  async touchSessionActivity(
    sessionId: string,
    timestamp = new Date().toISOString(),
  ): Promise<SessionRecord | undefined> {
    return this.mutate(async () => {
      const session = this.sessions.sessions[sessionId];
      if (!session) {
        return undefined;
      }
      session.lastSeenAt = timestamp;
      this.schedulePersist(this.sessionFile);
      return session;
    });
  }

  async hideSessionFromHistory(
    sessionId: string,
  ): Promise<SessionRecord | undefined> {
    return this.mutate(async () => {
      const session = this.sessions.sessions[sessionId];
      if (
        !session ||
        session.managedByAssistant !== true ||
        session.sessionId.startsWith("managed-terminal-")
      ) {
        return undefined;
      }
      session.historyHiddenAt ??= new Date().toISOString();
      await this.writeJson(this.sessionFile, this.sessions);
      return session;
    });
  }

  async replaceSessionReferences(
    sourceSessionId: string,
    targetSessionId: string,
  ): Promise<void> {
    if (sourceSessionId === targetSessionId) {
      return;
    }
    await this.mutate(async () => {
      const sourceSession = this.sessions.sessions[sourceSessionId];
      const targetSession = this.sessions.sessions[targetSessionId];
      if (sourceSession && targetSession) {
        targetSession.alias ??= sourceSession.alias;
        targetSession.managedByAssistant ??= sourceSession.managedByAssistant;
        targetSession.transcriptPath ??= sourceSession.transcriptPath;
        targetSession.feishuChatId ??= sourceSession.feishuChatId;
        targetSession.feishuChatName ??= sourceSession.feishuChatName;
        targetSession.feishuChatCreatedAt ??= sourceSession.feishuChatCreatedAt;
        targetSession.feishuChatError ??= sourceSession.feishuChatError;
        targetSession.feishuChatErrorAt ??= sourceSession.feishuChatErrorAt;
      }
      delete this.sessions.sessions[sourceSessionId];
      for (const route of Object.values(this.routes.messages)) {
        if (route.sessionId === sourceSessionId) {
          route.sessionId = targetSessionId;
        }
      }
      for (const approval of Object.values(this.approvals.requests)) {
        if (approval.sessionId === sourceSessionId) {
          approval.sessionId = targetSessionId;
        }
      }
      await Promise.all([
        this.writeJson(this.sessionFile, this.sessions),
        this.writeJson(this.routeFile, this.routes),
        this.writeJson(this.approvalFile, this.approvals),
      ]);
    });
  }

  async claimTurnNotification(
    sessionId: string,
    turnId: string,
    kind: "stop" | "error" = "stop",
    message = "",
  ): Promise<boolean> {
    return this.mutate(async () => {
      const session = this.sessions.sessions[sessionId];
      if (!session) {
        return false;
      }
      if (
        session.lastNotificationTurnId === turnId &&
        session.lastNotificationStatus !== "pending"
      ) {
        return false;
      }
      session.lastNotificationTurnId = turnId;
      session.lastNotificationStatus = "pending";
      session.pendingNotificationKind = kind;
      session.pendingNotificationMessage = message;
      await this.writeJson(this.sessionFile, this.sessions);
      return true;
    });
  }

  async setUniqueSessionAlias(
    sessionId: string,
    alias: string,
    reservedSessionIds: readonly string[],
  ): Promise<{
    session?: SessionRecord;
    conflict?: SessionRecord;
  }> {
    return this.mutate(async () => {
      const session = this.sessions.sessions[sessionId];
      if (!session) {
        return {};
      }
      const reserved = new Set(reservedSessionIds);
      const aliasKey = sessionAliasKey(alias);
      const conflict = Object.values(this.sessions.sessions).find(
        (candidate) =>
          candidate.sessionId !== sessionId &&
          reserved.has(candidate.sessionId) &&
          candidate.alias !== undefined &&
          sessionAliasKey(candidate.alias) === aliasKey,
      );
      if (conflict) {
        return { conflict };
      }
      session.alias = alias;
      await this.writeJson(this.sessionFile, this.sessions);
      return { session };
    });
  }

  async completeTurnNotification(sessionId: string, turnId: string): Promise<void> {
    await this.mutate(async () => {
      const session = this.sessions.sessions[sessionId];
      if (!session || session.lastNotificationTurnId !== turnId) {
        return;
      }
      session.lastNotificationStatus = "sent";
      delete session.pendingNotificationKind;
      delete session.pendingNotificationMessage;
      await this.writeJson(this.sessionFile, this.sessions);
    });
  }

  async releaseTurnNotification(sessionId: string, turnId: string): Promise<void> {
    await this.mutate(async () => {
      const session = this.sessions.sessions[sessionId];
      if (
        !session ||
        session.lastNotificationTurnId !== turnId ||
        session.lastNotificationStatus !== "pending"
      ) {
        return;
      }
      delete session.lastNotificationTurnId;
      delete session.lastNotificationStatus;
      delete session.pendingNotificationKind;
      delete session.pendingNotificationMessage;
      await this.writeJson(this.sessionFile, this.sessions);
    });
  }

  async addMessageRoute(route: MessageRoute): Promise<void> {
    await this.mutate(async () => {
      this.routes.messages[route.messageId] = route;
      const pruneChanges = this.pruneInMemory(Date.now());
      this.schedulePersist(this.routeFile);
      this.schedulePrunedFiles(pruneChanges);
    });
  }

  async claimInboundMessage(messageId: string): Promise<boolean> {
    return this.mutate(async () => {
      if (this.routes.processedInbound[messageId]) {
        return false;
      }
      this.routes.processedInbound[messageId] = new Date().toISOString();
      const pruneChanges = this.pruneInMemory(Date.now());
      await this.writeJson(this.routeFile, this.routes);
      this.schedulePrunedFiles(pruneChanges, this.routeFile);
      return true;
    });
  }

  findMessageRoute(messageIds: Array<string | undefined>): MessageRoute | undefined {
    for (const messageId of messageIds) {
      if (messageId && this.routes.messages[messageId]) {
        return this.routes.messages[messageId];
      }
    }
    return undefined;
  }

  getApproval(requestId: string): ApprovalRecord | undefined {
    return this.approvals.requests[requestId];
  }

  listApprovals(): ApprovalRecord[] {
    return Object.values(this.approvals.requests)
      .map((approval) => ({
        ...approval,
        messageIds: [...approval.messageIds],
      }))
      .sort(
        (left, right) =>
          Date.parse(left.createdAt) - Date.parse(right.createdAt),
      );
  }

  hasPendingApprovalForSession(sessionId: string): boolean {
    return Object.values(this.approvals.requests).some(
      (approval) => approval.sessionId === sessionId && approval.status === "pending",
    );
  }

  async createApproval(approval: ApprovalRecord): Promise<void> {
    await this.mutate(async () => {
      this.approvals.requests[approval.requestId] = approval;
      const pruneChanges = this.pruneInMemory(Date.now());
      this.schedulePersist(this.approvalFile);
      this.schedulePrunedFiles(pruneChanges);
    });
  }

  async addApprovalMessage(requestId: string, messageId: string): Promise<void> {
    await this.mutate(async () => {
      const approval = this.approvals.requests[requestId];
      if (!approval || approval.messageIds.includes(messageId)) {
        return;
      }
      approval.messageIds.push(messageId);
      const pruneChanges = this.pruneInMemory(Date.now());
      this.schedulePersist(this.approvalFile);
      this.schedulePrunedFiles(pruneChanges);
    });
  }

  async requireManualApproval(requestId: string): Promise<void> {
    await this.mutate(async () => {
      const approval = this.approvals.requests[requestId];
      if (
        !approval ||
        approval.status !== "pending" ||
        approval.requiresManualApproval === true
      ) {
        return;
      }
      approval.requiresManualApproval = true;
      const pruneChanges = this.pruneInMemory(Date.now());
      this.schedulePersist(this.approvalFile);
      this.schedulePrunedFiles(pruneChanges);
    });
  }

  async requestDesktopApproval(
    requestId: string,
  ): Promise<ApprovalRecord | undefined> {
    return this.mutate(async () => {
      const approval = this.approvals.requests[requestId];
      if (!approval || approval.status !== "pending") {
        return undefined;
      }
      if (approval.desktopApprovalRequested !== true) {
        approval.desktopApprovalRequested = true;
        const pruneChanges = this.pruneInMemory(Date.now());
        this.schedulePersist(this.approvalFile);
        this.schedulePrunedFiles(pruneChanges);
      }
      return approval;
    });
  }

  async claimApproval(requestId: string): Promise<ApprovalRecord | undefined> {
    return this.mutate(async () => {
      const approval = this.approvals.requests[requestId];
      if (
        !approval ||
        approval.status !== "pending" ||
        this.approvalClaims.has(requestId)
      ) {
        return undefined;
      }
      this.approvalClaims.add(requestId);
      return approval;
    });
  }

  async releaseApprovalClaim(requestId: string): Promise<void> {
    await this.mutate(async () => {
      this.approvalClaims.delete(requestId);
    });
  }

  async resolveClaimedApproval(
    requestId: string,
    resolution: ApprovalResolution,
  ): Promise<ApprovalRecord | undefined> {
    return this.mutate(async () => {
      const approval = this.approvals.requests[requestId];
      if (
        !approval ||
        approval.status !== "pending" ||
        !this.approvalClaims.has(requestId)
      ) {
        this.approvalClaims.delete(requestId);
        return undefined;
      }
      approval.status = "resolved";
      approval.resolution = resolution;
      approval.resolvedAt = new Date().toISOString();
      this.approvalClaims.delete(requestId);
      const pruneChanges = this.pruneInMemory(Date.now());
      this.schedulePersist(this.approvalFile);
      this.schedulePrunedFiles(pruneChanges);
      return approval;
    });
  }

  private pruneInMemory(
    now: number,
  ): { routesChanged: boolean; approvalsChanged: boolean } {
    let routesChanged = false;
    let approvalsChanged = false;
    const routeCutoff = now - 7 * 24 * 60 * 60 * 1000;
    for (const [messageId, route] of Object.entries(this.routes.messages)) {
      if (Date.parse(route.createdAt) < routeCutoff) {
        delete this.routes.messages[messageId];
        routesChanged = true;
      }
    }
    for (const [messageId, processedAt] of Object.entries(this.routes.processedInbound)) {
      if (Date.parse(processedAt) < routeCutoff) {
        delete this.routes.processedInbound[messageId];
        routesChanged = true;
      }
    }

    const approvalCutoff = now - 7 * 24 * 60 * 60 * 1000;
    for (const [requestId, approval] of Object.entries(this.approvals.requests)) {
      const referenceTime = approval.resolvedAt ?? approval.createdAt;
      if (Date.parse(referenceTime) < approvalCutoff) {
        delete this.approvals.requests[requestId];
        this.approvalClaims.delete(requestId);
        approvalsChanged = true;
      }
    }
    return { routesChanged, approvalsChanged };
  }

  private async readJson<T>(
    filePath: string,
    fallback: T,
    validate: (value: unknown) => boolean,
  ): Promise<T> {
    return await this.persistence.read(filePath, fallback, validate);
  }

  private async writeJson(filePath: string, value: unknown): Promise<void> {
    await this.persistence.write(filePath, value);
  }

  private mutate<T>(operation: () => Promise<T>): Promise<T> {
    const result = this.mutationQueue.then(operation, operation);
    this.mutationQueue = result.then(
      () => undefined,
      () => undefined,
    );
    return result;
  }

  private schedulePersist(filePath: string): void {
    this.persistence.schedule(filePath);
  }

  private schedulePrunedFiles(
    changes: { routesChanged: boolean; approvalsChanged: boolean },
    alreadyPersisted?: string,
  ): void {
    if (changes.routesChanged && alreadyPersisted !== this.routeFile) {
      this.schedulePersist(this.routeFile);
    }
    if (changes.approvalsChanged && alreadyPersisted !== this.approvalFile) {
      this.schedulePersist(this.approvalFile);
    }
  }

  /** 立即把待写文件全部落盘。适用于进程退出前的收尾和测试。 */
  async flushPending(): Promise<void> {
    await this.persistence.flushPending();
  }

  /** 停止后台定时器并确保所有排队写入已经落盘。 */
  async close(): Promise<void> {
    await this.persistence.close();
  }

  private inMemoryState(filePath: string): unknown {
    if (filePath === this.sessionFile) {
      return this.sessions;
    }
    if (filePath === this.routeFile) {
      return this.routes;
    }
    if (filePath === this.approvalFile) {
      return this.approvals;
    }
    if (filePath === this.bindingFile) {
      return this.bindings;
    }
    if (filePath === this.settingsFile) {
      return this.settings;
    }
    return undefined;
  }

  /** 已结束且超过保留期的会话，从内存中移除；返回是否发生清理。 */
  private pruneEndedSessions(now: number): boolean {
    const cutoff = now - this.endedSessionRetentionMs;
    let changed = false;
    for (const [sessionId, session] of Object.entries(this.sessions.sessions)) {
      const reference = session.endedAt ?? session.lastSeenAt;
      if (
        session.status === "ended" &&
        reference &&
        Date.parse(reference) < cutoff
      ) {
        delete this.sessions.sessions[sessionId];
        changed = true;
      }
    }
    return changed;
  }

  private startSafetyFlush(): void {
    this.persistence.startSafetyFlush();
  }

  private performSafetyMaintenance(): void {
    const pruneChanges = this.pruneInMemory(Date.now());
    this.schedulePrunedFiles(pruneChanges);
    if (this.pruneEndedSessions(Date.now())) {
      this.schedulePersist(this.sessionFile);
    }
  }
}

function createPairingCode(): string {
  return randomBytes(5).toString("hex").toUpperCase();
}
