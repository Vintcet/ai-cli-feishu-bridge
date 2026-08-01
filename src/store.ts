import { randomBytes } from "node:crypto";
import { mkdir, readFile, rename, writeFile } from "node:fs/promises";
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
import { projectNameFromCwd, shortSessionId, stringifyModel } from "./domain.js";

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

  private readonly persistDebounceMs: number;
  private readonly endedSessionRetentionMs: number;
  private readonly defaultWorkspaceRoot: string;
  private readonly dirtyFiles = new Set<string>();
  private flushTimer: ReturnType<typeof setTimeout> | undefined;
  private safetyTimer: ReturnType<typeof setInterval> | undefined;
  private flushChain: Promise<void> | undefined;

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
    this.persistDebounceMs = options.persistDebounceMs ?? defaultPersistDebounceMs;
    this.endedSessionRetentionMs =
      options.endedSessionRetentionMs ?? defaultEndedSessionRetentionMs;
    this.defaultWorkspaceRoot = options.defaultWorkspaceRoot
      ? path.resolve(options.defaultWorkspaceRoot)
      : "";
  }

  async init(): Promise<void> {
    await mkdir(this.dataDirectory, { recursive: true });
    const [bindings, sessions, routes, approvals, settings] = await Promise.all([
      this.readJson(this.bindingFile, emptyBindings()),
      this.readJson(this.sessionFile, emptySessions()),
      this.readJson(this.routeFile, emptyRoutes()),
      this.readJson(this.approvalFile, emptyApprovals()),
      this.readJson(this.settingsFile, defaultSettings(this.defaultWorkspaceRoot)),
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
      const hasFeishuChatError = Object.prototype.hasOwnProperty.call(
        input,
        "feishuChatError",
      );
      const hasFeishuChatErrorAt = Object.prototype.hasOwnProperty.call(
        input,
        "feishuChatErrorAt",
      );
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
        managedByAssistant:
          input.managedByAssistant ??
          current?.managedByAssistant ??
          Boolean(input.managedTerminalId),
        historyHiddenAt: current?.historyHiddenAt,
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

  async markStopNotified(sessionId: string, turnId: string): Promise<void> {
    await this.mutate(async () => {
      const session = this.sessions.sessions[sessionId];
      if (!session) {
        return;
      }
      session.lastNotificationTurnId = turnId;
      await this.writeJson(this.sessionFile, this.sessions);
    });
  }

  async addMessageRoute(route: MessageRoute): Promise<void> {
    await this.mutate(async () => {
      this.routes.messages[route.messageId] = route;
      this.pruneInMemory(Date.now());
      await this.writeJson(this.routeFile, this.routes);
    });
  }

  async claimInboundMessage(messageId: string): Promise<boolean> {
    return this.mutate(async () => {
      if (this.routes.processedInbound[messageId]) {
        return false;
      }
      this.routes.processedInbound[messageId] = new Date().toISOString();
      this.pruneInMemory(Date.now());
      await this.writeJson(this.routeFile, this.routes);
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
      await this.writeJson(this.approvalFile, this.approvals);
    });
  }

  async addApprovalMessage(requestId: string, messageId: string): Promise<void> {
    await this.mutate(async () => {
      const approval = this.approvals.requests[requestId];
      if (!approval || approval.messageIds.includes(messageId)) {
        return;
      }
      approval.messageIds.push(messageId);
      await this.writeJson(this.approvalFile, this.approvals);
    });
  }

  async resolveApproval(
    requestId: string,
    resolution: ApprovalResolution,
  ): Promise<ApprovalRecord | undefined> {
    return this.mutate(async () => {
      const approval = this.approvals.requests[requestId];
      if (!approval || approval.status !== "pending") {
        return undefined;
      }
      approval.status = "resolved";
      approval.resolution = resolution;
      approval.resolvedAt = new Date().toISOString();
      await this.writeJson(this.approvalFile, this.approvals);
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
      if (approval.status !== "pending" && Date.parse(referenceTime) < approvalCutoff) {
        delete this.approvals.requests[requestId];
        approvalsChanged = true;
      }
    }
    return { routesChanged, approvalsChanged };
  }

  private async readJson<T>(filePath: string, fallback: T): Promise<T> {
    try {
      return JSON.parse(await readFile(filePath, "utf8")) as T;
    } catch (error) {
      const code = (error as NodeJS.ErrnoException).code;
      if (code !== "ENOENT") {
        console.warn(`[store] Could not read ${path.basename(filePath)}; using defaults.`);
      }
      return fallback;
    }
  }

  private async writeJson(filePath: string, value: unknown): Promise<void> {
    await mkdir(this.dataDirectory, { recursive: true });
    const temporaryPath = `${filePath}.${process.pid}.tmp`;
    await writeFile(temporaryPath, `${JSON.stringify(value)}\n`, "utf8");
    await rename(temporaryPath, filePath);
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
    this.dirtyFiles.add(filePath);
    if (this.flushTimer) {
      return;
    }
    this.flushTimer = setTimeout(() => {
      this.flushTimer = undefined;
      void this.flushPending();
    }, this.persistDebounceMs);
    this.flushTimer.unref?.();
  }

  /** 立即把待写文件全部落盘。适用于进程退出前的收尾和测试。 */
  async flushPending(): Promise<void> {
    if (this.flushChain) {
      return this.flushChain;
    }
    const run = async (): Promise<void> => {
      while (this.dirtyFiles.size > 0) {
        const files = [...this.dirtyFiles];
        this.dirtyFiles.clear();
        await this.mutate(async () => {
          await Promise.all(
            files.map((filePath) =>
              this.writeJson(filePath, this.inMemoryState(filePath)),
            ),
          );
        });
      }
    };
    this.flushChain = run().finally(() => {
      this.flushChain = undefined;
    });
    return this.flushChain;
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
        !session.feishuChatId &&
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
    if (this.safetyTimer) {
      return;
    }
    this.safetyTimer = setInterval(() => {
      if (this.pruneEndedSessions(Date.now())) {
        this.schedulePersist(this.sessionFile);
      }
      void this.flushPending();
    }, 5_000);
    this.safetyTimer.unref?.();
  }
}

function createPairingCode(): string {
  return randomBytes(5).toString("hex").toUpperCase();
}
