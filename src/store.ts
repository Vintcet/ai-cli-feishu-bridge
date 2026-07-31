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
  SessionRecord,
  SessionStatus,
  SessionStore,
} from "./domain.js";
import { projectNameFromCwd, shortSessionId } from "./domain.js";

const emptyBindings = (): BindingStore => ({ users: {} });
const emptySessions = (): SessionStore => ({ sessions: {} });
const emptyRoutes = (): RouteStore => ({ messages: {}, processedInbound: {} });
const emptyApprovals = (): ApprovalStore => ({ requests: {} });
const defaultSettings = (): BridgeSettings => ({
  notifyActivity: false,
  autoRetryErrors: false,
  autoApprove: false,
});

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

  constructor(private readonly dataDirectory: string) {
    this.bindingFile = path.join(dataDirectory, "bindings.json");
    this.sessionFile = path.join(dataDirectory, "sessions.json");
    this.routeFile = path.join(dataDirectory, "message-routes.json");
    this.approvalFile = path.join(dataDirectory, "approvals.json");
    this.controlTokenFile = path.join(dataDirectory, "control-token.json");
    this.settingsFile = path.join(dataDirectory, "settings.json");
  }

  async init(): Promise<void> {
    await mkdir(this.dataDirectory, { recursive: true });
    const [bindings, sessions, routes, approvals, settings] = await Promise.all([
      this.readJson(this.bindingFile, emptyBindings()),
      this.readJson(this.sessionFile, emptySessions()),
      this.readJson(this.routeFile, emptyRoutes()),
      this.readJson(this.approvalFile, emptyApprovals()),
      this.readJson(this.settingsFile, defaultSettings()),
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
      notifyActivity: loadedSettings.notifyActivity === true,
      autoRetryErrors: loadedSettings.autoRetryErrors === true,
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
    await Promise.all([
      bindingsChanged ? this.writeJson(this.bindingFile, this.bindings) : Promise.resolve(),
      routesChanged ? this.writeJson(this.routeFile, this.routes) : Promise.resolve(),
      approvalsChanged ? this.writeJson(this.approvalFile, this.approvals) : Promise.resolve(),
      approvalsChanged || sessionsChanged
        ? this.writeJson(this.sessionFile, this.sessions)
        : Promise.resolve(),
    ]);
  }

  getSettings(): BridgeSettings {
    return { ...this.settings };
  }

  async updateSettings(value: Partial<BridgeSettings>): Promise<BridgeSettings> {
    return this.mutate(async () => {
      this.settings = {
        notifyActivity:
          typeof value.notifyActivity === "boolean"
            ? value.notifyActivity
            : this.settings.notifyActivity,
        autoRetryErrors:
          typeof value.autoRetryErrors === "boolean"
            ? value.autoRetryErrors
            : this.settings.autoRetryErrors,
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
    clientProcessId?: number | null;
    clientProcessStartedAt?: string | null;
    managedTerminalId?: string | null;
    managedTerminalElevated?: boolean | null;
    openedAt?: string;
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
      const next: SessionRecord = {
        sessionId: input.sessionId,
        shortId: shortSessionId(input.sessionId),
        alias: input.alias ?? current?.alias,
        cwd: input.cwd,
        projectName: projectNameFromCwd(input.cwd),
        model: input.model ?? current?.model,
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
      };
      this.sessions.sessions[input.sessionId] = next;
      await this.writeJson(this.sessionFile, this.sessions);
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

  async replaceSessionReferences(
    sourceSessionId: string,
    targetSessionId: string,
  ): Promise<void> {
    if (sourceSessionId === targetSessionId) {
      return;
    }
    await this.mutate(async () => {
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
    await writeFile(temporaryPath, `${JSON.stringify(value, null, 2)}\n`, "utf8");
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
}

function createPairingCode(): string {
  return randomBytes(5).toString("hex").toUpperCase();
}
