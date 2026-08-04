import { execFile } from "node:child_process";
import { createServer } from "node:net";
import path from "node:path";
import { promisify } from "node:util";

import { OpenCodeClient } from "./opencode-client.js";
import type {
  OpenCodeEventHandlers,
  OpenCodePermission,
  OpenCodePermissionResponse,
  OpenCodeQuestionRequest,
  OpenCodeSession,
} from "./opencode-client.js";

const execFileAsync = promisify(execFile);

export interface OpenCodeInstance {
  port: number;
  cwd: string;
  client: OpenCodeClient;
  connectedAt: string;
  allowHistoricalFallback: boolean;
  closeSubscription: () => void;
}

export interface OpenCodeLaunchResult {
  port: number;
}

export interface OpenCodeManagerHandlers {
  onInstanceConnected: (port: number, cwd: string) => void;
  onInstanceDisconnected: (port: number) => void;
  eventHandlers: OpenCodeEventHandlers;
}

interface OpenCodeConnectOptions {
  reconnecting?: boolean;
  expectedClient?: OpenCodeClient;
}

export class OpenCodeManager {
  private readonly instances = new Map<number, OpenCodeInstance>();
  private readonly pendingPorts = new Set<number>();
  private readonly pendingSessionIds = new Map<number, string>();
  private readonly assistantLaunchPorts = new Set<number>();
  private readonly retryTimers = new Map<number, ReturnType<typeof setTimeout>>();
  private readonly subscriptionRetryTimers = new Map<
    number,
    ReturnType<typeof setTimeout>
  >();
  private readonly subscriptionRetryAttempts = new Map<number, number>();
  private readonly subscriptionConnectedAt = new Map<number, number>();
  private readonly sessionPorts = new Map<string, number>();
  private readonly foregroundSessions = new Map<number, string>();
  private readonly sessionMetadata = new Map<string, OpenCodeSession>();
  private readonly pendingPermissionKeys = new Set<string>();
  private readonly pendingQuestionKeys = new Set<string>();
  private readonly connectingPorts = new Set<number>();
  private readonly discoveryMisses = new Map<number, number>();
  private readonly basePort: number;
  private readonly maxPort: number;
  private readonly autoDiscover: boolean;
  private readonly scanIntervalMs: number;
  private readonly subscriptionRetryBaseMs: number;
  private readonly subscriptionRetryMaxMs: number;
  private readonly subscriptionStableMs: number;
  private readonly enumerateLocalPorts: () => Promise<number[]>;
  private readonly isLocalPortAvailable: (port: number) => Promise<boolean>;
  private discoverTimer: ReturnType<typeof setTimeout> | undefined;
  private discoverRunning = false;
  private discoveryActive = false;

  constructor(
    private readonly handlers: OpenCodeManagerHandlers,
    options: {
      basePort?: number;
      maxPort?: number;
      autoDiscover?: boolean;
      scanIntervalMs?: number;
      subscriptionRetryBaseMs?: number;
      subscriptionRetryMaxMs?: number;
      subscriptionStableMs?: number;
      enumerateLocalPorts?: () => Promise<number[]>;
      isLocalPortAvailable?: (port: number) => Promise<boolean>;
    } = {},
  ) {
    this.basePort = options.basePort ?? 5100;
    this.maxPort = options.maxPort ?? 5999;
    this.autoDiscover = options.autoDiscover ?? true;
    this.scanIntervalMs = options.scanIntervalMs ?? 20_000;
    this.subscriptionRetryBaseMs = options.subscriptionRetryBaseMs ?? 1_000;
    this.subscriptionRetryMaxMs = options.subscriptionRetryMaxMs ?? 30_000;
    this.subscriptionStableMs = options.subscriptionStableMs ?? 30_000;
    this.enumerateLocalPorts = options.enumerateLocalPorts ?? defaultEnumerateLocalPorts;
    this.isLocalPortAvailable = options.isLocalPortAvailable ?? defaultIsLocalPortAvailable;
  }

  listInstances(): OpenCodeInstance[] {
    return [...this.instances.values()].sort((left, right) => left.port - right.port);
  }

  getInstance(port: number): OpenCodeInstance | undefined {
    return this.instances.get(port);
  }

  findInstanceBySession(sessionId: string): OpenCodeInstance | undefined {
    const port = this.sessionPorts.get(sessionId);
    if (port === undefined) {
      return undefined;
    }
    return this.instances.get(port);
  }

  findActiveInstanceBySession(sessionId: string): OpenCodeInstance | undefined {
    for (const [port, activeSessionId] of this.foregroundSessions) {
      if (activeSessionId === sessionId) {
        return this.instances.get(port);
      }
    }
    return undefined;
  }

  hasPendingSession(sessionId: string): boolean {
    return [...this.pendingSessionIds.values()].includes(sessionId);
  }

  async launch(cwd: string, sessionId?: string): Promise<OpenCodeLaunchResult> {
    const port = await this.allocatePort();
    this.pendingPorts.add(port);
    this.assistantLaunchPorts.add(port);
    if (sessionId) {
      this.pendingSessionIds.set(port, sessionId);
    }
    void this.retryConnect(port, cwd, 0);
    return { port };
  }

  async register(port: number, cwd: string): Promise<void> {
    this.pendingPorts.add(port);
    this.clearSubscriptionRetry(port, true);
    await this.connect(port, cwd);
  }

  async unregister(port: number): Promise<void> {
    const instance = this.instances.get(port);
    if (instance) {
      instance.closeSubscription();
      this.instances.delete(port);
    }
    for (const [sessionId, sessionPort] of this.sessionPorts) {
      if (sessionPort === port) {
        this.sessionPorts.delete(sessionId);
        this.sessionMetadata.delete(sessionId);
      }
    }
    this.foregroundSessions.delete(port);
    this.pendingPorts.delete(port);
    this.pendingSessionIds.delete(port);
    this.assistantLaunchPorts.delete(port);
    const timer = this.retryTimers.get(port);
    if (timer) {
      clearTimeout(timer);
      this.retryTimers.delete(port);
    }
    this.clearSubscriptionRetry(port, true);
    this.subscriptionConnectedAt.delete(port);
    this.clearPendingInteractionKeys(port);
    this.discoveryMisses.delete(port);
    if (instance) {
      this.handlers.onInstanceDisconnected(port);
    }
  }

  private async allocatePort(): Promise<number> {
    const listeningPorts = await this.enumerateLocalPorts();
    const usedPorts = new Set<number>(listeningPorts);
    for (const port of this.instances.keys()) {
      usedPorts.add(port);
    }
    for (const port of this.pendingPorts) {
      usedPorts.add(port);
    }
    for (let port = this.basePort; port <= this.maxPort; port += 1) {
      if (usedPorts.has(port)) continue;
      this.pendingPorts.add(port);
      let available = false;
      try {
        available = await this.isLocalPortAvailable(port);
      } catch {
        available = false;
      }
      if (available) {
        return port;
      }
      this.pendingPorts.delete(port);
      usedPorts.add(port);
    }
    throw new Error(`opencode 端口池已用尽（${this.basePort}-${this.maxPort}）`);
  }

  private async retryConnect(
    port: number,
    cwd: string,
    attempt: number,
  ): Promise<void> {
    if (!this.pendingPorts.has(port)) {
      return;
    }
    try {
      await this.connect(port, cwd);
    } catch {
      if (!this.pendingPorts.has(port)) {
        return;
      }
      const maxAttempts = 120;
      if (attempt >= maxAttempts) {
        console.warn(
          `[opencode] 端口 ${port} 在 ${maxAttempts} 次尝试后仍未就绪，停止重试。`,
        );
        this.pendingPorts.delete(port);
        this.handlers.onInstanceDisconnected(port);
        return;
      }
      const delay = attempt < 3 ? 1000 : 5000;
      const timer = setTimeout(() => {
        this.retryTimers.delete(port);
        void this.retryConnect(port, cwd, attempt + 1);
      }, delay);
      timer.unref?.();
      this.retryTimers.set(port, timer);
    }
  }

  private async connect(
    port: number,
    cwd: string,
    options: OpenCodeConnectOptions = {},
  ): Promise<void> {
    const client = new OpenCodeClient(`http://127.0.0.1:${port}`, cwd);
    const healthy = await client.health();
    if (!healthy) {
      throw new Error(`opencode 端口 ${port} 未就绪`);
    }
    if (
      options.expectedClient &&
      this.instances.get(port)?.client !== options.expectedClient
    ) {
      return;
    }
    const wrappedHandlers: OpenCodeEventHandlers = {
      ...this.handlers.eventHandlers,
      onSessionCreated: (session) => {
        this.observeSession(port, cwd, session, true);
        this.handlers.eventHandlers.onSessionCreated?.(session);
      },
      onSessionUpdated: (session) => {
        this.observeSession(port, cwd, session, true);
        this.handlers.eventHandlers.onSessionUpdated?.(session);
      },
      onSessionDeleted: (sessionId) => {
        this.forgetSession(sessionId, port);
        this.handlers.eventHandlers.onSessionDeleted?.(sessionId);
      },
      onSessionIdle: (sessionId) => {
        this.observeSessionActivity(port, cwd, sessionId);
        this.handlers.eventHandlers.onSessionIdle?.(sessionId);
      },
      onSessionError: (sessionId, error) => {
        this.observeSessionActivity(port, cwd, sessionId);
        this.handlers.eventHandlers.onSessionError?.(sessionId, error);
      },
      onSessionStatus: (sessionId, status) => {
        this.observeSessionActivity(port, cwd, sessionId);
        this.handlers.eventHandlers.onSessionStatus?.(sessionId, status);
      },
      onSessionCompacted: (sessionId) => {
        this.observeSessionActivity(port, cwd, sessionId);
        this.handlers.eventHandlers.onSessionCompacted?.(sessionId);
      },
      onMessageUpdated: (message) => {
        if (message.sessionID) {
          this.observeSessionActivity(port, cwd, message.sessionID);
        }
        this.handlers.eventHandlers.onMessageUpdated?.(message);
      },
      onMessagePartUpdated: (properties) => {
        const sessionId = properties.sessionID ?? properties.part?.sessionID;
        if (sessionId) {
          this.observeSessionActivity(port, cwd, sessionId);
        }
        this.handlers.eventHandlers.onMessagePartUpdated?.(properties);
      },
      onPermissionAsked: (permission) => {
        this.dispatchPermission(port, permission, "asked");
      },
      onPermissionUpdated: (permission) => {
        this.dispatchPermission(port, permission, "updated");
      },
      onQuestionAsked: (request) => {
        this.dispatchQuestion(port, request);
      },
      onPermissionReplied: (reply) => {
        this.rememberSession(port, reply.sessionID);
        this.pendingPermissionKeys.delete(
          this.pendingInteractionKey(port, reply.requestID),
        );
        this.handlers.eventHandlers.onPermissionReplied?.(reply);
      },
      onQuestionReplied: (reply) => {
        this.rememberSession(port, reply.sessionID);
        this.pendingQuestionKeys.delete(
          this.pendingInteractionKey(port, reply.requestID),
        );
        this.handlers.eventHandlers.onQuestionReplied?.(reply);
      },
      onQuestionRejected: (rejection) => {
        this.rememberSession(port, rejection.sessionID);
        this.pendingQuestionKeys.delete(
          this.pendingInteractionKey(port, rejection.requestID),
        );
        this.handlers.eventHandlers.onQuestionRejected?.(rejection);
      },
      onDisconnected: () => {
        void this.handleInstanceSubscriptionClosed(port, client).catch((error) => {
          console.warn(`[opencode] 端口 ${port} 订阅结束后处理失败：`, error);
        });
      },
    };
    const previous = this.instances.get(port);
    const launchedByAssistant = this.assistantLaunchPorts.has(port);
    const pendingSessionId = this.pendingSessionIds.get(port);
    const instance: OpenCodeInstance = {
      port,
      cwd,
      client,
      connectedAt: new Date().toISOString(),
      allowHistoricalFallback:
        previous?.allowHistoricalFallback ?? !launchedByAssistant,
      closeSubscription: () => {},
    };
    this.instances.set(port, instance);
    const { close: closeSubscription } = client.subscribe(wrappedHandlers);
    instance.closeSubscription = closeSubscription;
    previous?.closeSubscription();
    this.subscriptionConnectedAt.set(port, Date.now());
    this.pendingPorts.delete(port);
    this.pendingSessionIds.delete(port);
    this.assistantLaunchPorts.delete(port);
    if (options.reconnecting) {
      void this.seedPendingInteractions(instance);
    } else {
      this.clearSubscriptionRetry(port, true);
      void this.bootstrapInstance(instance, pendingSessionId);
      this.handlers.onInstanceConnected(port, cwd);
    }
  }

  private async bootstrapInstance(
    instance: OpenCodeInstance,
    pendingSessionId?: string,
  ): Promise<void> {
    if (pendingSessionId) {
      await this.seedPendingSession(instance, pendingSessionId);
    }
    const seededActive = await this.seedActiveSession(instance);
    if (
      !pendingSessionId &&
      !seededActive &&
      instance.allowHistoricalFallback &&
      !this.foregroundSessions.has(instance.port)
    ) {
      await this.seedRecentSessionCandidate(instance);
    }
    await this.seedPendingInteractions(instance);
  }

  private async seedActiveSession(instance: OpenCodeInstance): Promise<boolean> {
    try {
      const sessionIds = await instance.client.listActiveSessionIds();
      const sessions = (
        await Promise.all(sessionIds.map((sessionId) => instance.client.getSession(sessionId)))
      ).filter((session): session is OpenCodeSession => Boolean(session));
      const candidate = selectMostRecentSession(
        sessions.filter((session) => isForegroundSession(session, instance.cwd)),
      );
      if (!candidate || this.foregroundSessions.has(instance.port)) {
        return this.foregroundSessions.has(instance.port);
      }
      this.observeSession(instance.port, instance.cwd, candidate, true);
      this.handlers.eventHandlers.onSessionCreated?.(candidate);
      return true;
    } catch (error) {
      console.warn(`[opencode] 端口 ${instance.port} 同步运行中会话失败：`, error);
      return this.foregroundSessions.has(instance.port);
    }
  }

  private async seedRecentSessionCandidate(instance: OpenCodeInstance): Promise<void> {
    try {
      const sessions = await instance.client.listSessions();
      const candidate = selectMostRecentSession(
        sessions.filter((session) => isForegroundSession(session, instance.cwd)),
      );
      if (!candidate || this.foregroundSessions.has(instance.port)) {
        return;
      }
      this.observeSession(instance.port, instance.cwd, candidate, true);
      this.handlers.eventHandlers.onSessionCreated?.(candidate);
      console.log(
        `[opencode] 端口 ${instance.port} 自动发现时暂用最近会话 #${candidate.id}。`,
      );
    } catch (error) {
      console.warn(`[opencode] 端口 ${instance.port} 同步最近会话失败：`, error);
    }
  }

  private async seedPendingSession(
    instance: OpenCodeInstance,
    sessionId: string,
  ): Promise<void> {
    try {
      const session = await instance.client.getSession(sessionId);
      if (!session) {
        console.warn(
          `[opencode] 端口 ${instance.port} 恢复会话 ${sessionId} 未找到，跳过登记。`,
        );
        return;
      }
      if (this.foregroundSessions.get(instance.port) === session.id) {
        return;
      }
      this.observeSession(instance.port, instance.cwd, session, true, true);
      this.handlers.eventHandlers.onSessionCreated?.(session);
      console.log(
        `[opencode] 端口 ${instance.port} 登记恢复的会话 #${session.id}。`,
      );
    } catch (error) {
      console.warn(`[opencode] 端口 ${instance.port} 登记恢复会话失败：`, error);
    }
  }

  private async seedPendingInteractions(instance: OpenCodeInstance): Promise<void> {
    const [permissionsResult, questionsResult] = await Promise.allSettled([
      instance.client.listPermissions(),
      instance.client.listQuestions(),
    ]);
    if (permissionsResult.status === "fulfilled") {
      const permissions = permissionsResult.value;
      for (const permission of permissions) {
        this.dispatchPermission(instance.port, permission, "asked");
      }
    } else {
      console.warn(
        `[opencode] 端口 ${instance.port} 同步待处理权限失败：`,
        permissionsResult.reason,
      );
    }
    if (questionsResult.status === "fulfilled") {
      const questions = questionsResult.value;
      for (const question of questions) {
        this.dispatchQuestion(instance.port, question);
      }
    } else {
      console.warn(
        `[opencode] 端口 ${instance.port} 同步待处理问题失败：`,
        questionsResult.reason,
      );
    }
  }

  private dispatchPermission(
    port: number,
    permission: OpenCodePermission,
    event: "asked" | "updated",
  ): void {
    if (permission.sessionID) {
      this.rememberSession(port, permission.sessionID);
    }
    const key = this.pendingInteractionKey(port, permission.id);
    if (this.pendingPermissionKeys.has(key)) {
      return;
    }
    this.pendingPermissionKeys.add(key);
    if (event === "updated") {
      this.handlers.eventHandlers.onPermissionUpdated?.(permission);
    } else {
      this.handlers.eventHandlers.onPermissionAsked?.(permission);
    }
  }

  private dispatchQuestion(port: number, request: OpenCodeQuestionRequest): void {
    this.rememberSession(port, request.sessionID);
    const key = this.pendingInteractionKey(port, request.id);
    if (this.pendingQuestionKeys.has(key)) {
      return;
    }
    this.pendingQuestionKeys.add(key);
    this.handlers.eventHandlers.onQuestionAsked?.(request);
  }

  private pendingInteractionKey(port: number, interactionId: string): string {
    return `${port}:${interactionId}`;
  }

  private clearPendingInteractionKeys(port: number): void {
    const prefix = `${port}:`;
    for (const key of this.pendingPermissionKeys) {
      if (key.startsWith(prefix)) {
        this.pendingPermissionKeys.delete(key);
      }
    }
    for (const key of this.pendingQuestionKeys) {
      if (key.startsWith(prefix)) {
        this.pendingQuestionKeys.delete(key);
      }
    }
  }

  rememberSession(port: number, sessionId: string): void {
    this.sessionPorts.set(sessionId, port);
  }

  forgetSession(sessionId: string, port?: number): void {
    const ownsMapping = port === undefined || this.sessionPorts.get(sessionId) === port;
    if (ownsMapping) {
      this.sessionPorts.delete(sessionId);
      this.sessionMetadata.delete(sessionId);
    }
    for (const [activePort, activeSessionId] of this.foregroundSessions) {
      if (
        activeSessionId === sessionId &&
        (port === undefined || activePort === port)
      ) {
        this.foregroundSessions.delete(activePort);
      }
    }
  }

  private observeSession(
    port: number,
    cwd: string,
    session: OpenCodeSession,
    activate: boolean,
    allowDifferentDirectory = false,
  ): void {
    this.sessionMetadata.set(session.id, session);
    this.rememberSession(port, session.id);
    if (
      activate &&
      (allowDifferentDirectory || isForegroundSession(session, cwd))
    ) {
      this.foregroundSessions.set(port, session.id);
    }
  }

  private observeSessionActivity(port: number, cwd: string, sessionId: string): void {
    this.rememberSession(port, sessionId);
    const session = this.sessionMetadata.get(sessionId);
    if (!session || isForegroundSession(session, cwd)) {
      this.foregroundSessions.set(port, sessionId);
    }
  }

  async sendPrompt(
    sessionId: string,
    text: string,
    options: {
      model?: string;
      agent?: string;
      noReply?: boolean;
    } = {},
  ): Promise<void> {
    const instance = this.findInstanceBySession(sessionId);
    if (!instance) {
      throw new Error("找不到对应的 opencode 实例");
    }
    await instance.client.sendPrompt(sessionId, text, options);
  }

  async replyPermission(
    sessionId: string,
    permissionId: string,
    response: OpenCodePermissionResponse,
  ): Promise<void> {
    const instance = await this.resolvePermissionInstance(sessionId);
    if (!instance) {
      throw new Error("找不到对应的 opencode 实例");
    }
    await instance.client.replyPermission(sessionId, permissionId, response);
    this.pendingPermissionKeys.delete(
      this.pendingInteractionKey(instance.port, permissionId),
    );
  }

  private async resolvePermissionInstance(
    sessionId: string,
  ): Promise<OpenCodeInstance | undefined> {
    const mapped = this.findInstanceBySession(sessionId);
    if (mapped) {
      return mapped;
    }
    for (const instance of this.instances.values()) {
      if (await instance.client.getSession(sessionId)) {
        this.rememberSession(instance.port, sessionId);
        return instance;
      }
    }
    if (this.instances.size === 1) {
      const instance = this.instances.values().next().value as
        | OpenCodeInstance
        | undefined;
      if (instance) {
        this.rememberSession(instance.port, sessionId);
      }
      return instance;
    }
    return undefined;
  }

  async replyQuestion(
    sessionId: string,
    requestId: string,
    answers: string[][],
  ): Promise<void> {
    const instance = this.findInstanceBySession(sessionId);
    if (!instance) {
      throw new Error("找不到对应的 opencode 实例");
    }
    await instance.client.replyQuestion(requestId, answers);
    this.pendingQuestionKeys.delete(
      this.pendingInteractionKey(instance.port, requestId),
    );
  }

  async rejectQuestion(sessionId: string, requestId: string): Promise<void> {
    const instance = this.findInstanceBySession(sessionId);
    if (!instance) {
      throw new Error("找不到对应的 opencode 实例");
    }
    await instance.client.rejectQuestion(requestId);
    this.pendingQuestionKeys.delete(
      this.pendingInteractionKey(instance.port, requestId),
    );
  }

  async listQuestions(sessionId: string): Promise<OpenCodeQuestionRequest[]> {
    const instance = this.findInstanceBySession(sessionId);
    if (!instance) {
      return [];
    }
    return instance.client.listQuestions();
  }

  async listSessions(port: number): Promise<OpenCodeSession[]> {
    const instance = this.getInstance(port);
    if (!instance) {
      return [];
    }
    return instance.client.listSessions();
  }

  async lastAssistantText(
    sessionId: string,
  ): Promise<{ text: string; hasError: boolean }> {
    const instance = this.findInstanceBySession(sessionId);
    if (!instance) {
      return { text: "", hasError: false };
    }
    const messages = await instance.client.listMessages(sessionId, 50);
    return instance.client.extractLastAssistantText(messages);
  }

  startAutoDiscovery(): void {
    if (!this.autoDiscover || this.discoveryActive) {
      return;
    }
    this.discoveryActive = true;
    const schedule = (): void => {
      if (!this.discoveryActive || this.discoverTimer) {
        return;
      }
      this.discoverTimer = setTimeout(() => {
        this.discoverTimer = undefined;
        if (!this.discoveryActive) {
          return;
        }
        void this.runDiscoveryPass().finally(() => {
          if (this.discoveryActive) {
            schedule();
          }
        });
      }, this.scanIntervalMs);
      this.discoverTimer.unref?.();
    };
    schedule();
  }

  stopAutoDiscovery(): void {
    this.discoveryActive = false;
    if (this.discoverTimer) {
      clearTimeout(this.discoverTimer);
      this.discoverTimer = undefined;
    }
  }

  private async runDiscoveryPass(): Promise<void> {
    if (this.discoverRunning) {
      return;
    }
    this.discoverRunning = true;
    try {
      const ports = await this.enumerateLocalPorts();
      const knownPorts = new Set<number>([...this.instances.keys(), ...this.pendingPorts]);
      for (const port of ports) {
        if (knownPorts.has(port) || this.connectingPorts.has(port)) {
          continue;
        }
        this.connectingPorts.add(port);
        void this.connectDiscovered(port).finally(() => this.connectingPorts.delete(port));
      }

      for (const instance of [...this.instances.values()]) {
        const healthy = await instance.client.probeHealth();
        if (healthy.healthy) {
          this.discoveryMisses.delete(instance.port);
          continue;
        }
        const misses = (this.discoveryMisses.get(instance.port) ?? 0) + 1;
        if (misses >= 3) {
          this.discoveryMisses.delete(instance.port);
          console.warn(`[opencode] 端口 ${instance.port} 已停止响应，自动移除。`);
          await this.unregister(instance.port);
        } else {
          this.discoveryMisses.set(instance.port, misses);
        }
      }
    } catch (error) {
      console.warn("[opencode] 自动发现一轮扫描失败：", error);
    } finally {
      this.discoverRunning = false;
    }
  }

  private async connectDiscovered(port: number): Promise<void> {
    try {
      const client = new OpenCodeClient(`http://127.0.0.1:${port}`);
      const health = await client.probeHealth();
      if (!health.healthy) {
        return;
      }
      const sessions = await client.listSessions();
      if (!Array.isArray(sessions)) {
        return;
      }
      const cwd = (await client.currentDirectory()) ?? "";
      await this.connect(port, cwd);
      console.log(`[opencode] 自动发现并连接端口 ${port} 的 opencode 实例。`);
    } catch {
      // 失败时留到下一轮扫描重试。
    }
  }

  private async handleInstanceSubscriptionClosed(
    port: number,
    disconnectedClient: OpenCodeClient,
  ): Promise<void> {
    const instance = this.instances.get(port);
    if (!instance || instance.client !== disconnectedClient) {
      return;
    }
    if (
      this.connectingPorts.has(port) ||
      this.subscriptionRetryTimers.has(port)
    ) {
      return;
    }
    const connectedAt = this.subscriptionConnectedAt.get(port) ?? Date.now();
    if (Date.now() - connectedAt >= this.subscriptionStableMs) {
      this.subscriptionRetryAttempts.delete(port);
    }
    const attempt = this.subscriptionRetryAttempts.get(port) ?? 0;
    const delay = Math.min(
      this.subscriptionRetryBaseMs * 2 ** Math.min(attempt, 20),
      this.subscriptionRetryMaxMs,
    );
    this.subscriptionRetryAttempts.set(port, attempt + 1);
    const timer = setTimeout(() => {
      this.subscriptionRetryTimers.delete(port);
      void this.reconnectSubscription(port, disconnectedClient);
    }, delay);
    timer.unref?.();
    this.subscriptionRetryTimers.set(port, timer);
  }

  private async reconnectSubscription(
    port: number,
    disconnectedClient: OpenCodeClient,
  ): Promise<void> {
    const instance = this.instances.get(port);
    if (!instance || instance.client !== disconnectedClient) {
      return;
    }
    if (this.connectingPorts.has(port)) {
      return;
    }
    this.connectingPorts.add(port);
    try {
      await this.connect(port, instance.cwd, {
        reconnecting: true,
        expectedClient: disconnectedClient,
      });
    } catch {
      if (this.instances.get(port)?.client === disconnectedClient) {
        console.warn(`[opencode] 端口 ${port} 订阅已断开且服务不可达，自动移除。`);
        await this.unregister(port);
      }
    } finally {
      this.connectingPorts.delete(port);
    }
  }

  private clearSubscriptionRetry(port: number, resetAttempts: boolean): void {
    const timer = this.subscriptionRetryTimers.get(port);
    if (timer) {
      clearTimeout(timer);
      this.subscriptionRetryTimers.delete(port);
    }
    if (resetAttempts) {
      this.subscriptionRetryAttempts.delete(port);
    }
  }
}

const listeningForeignAddresses = new Set(["0.0.0.0:0", "[::]:0", "*:*"]);
const localListenHosts = new Set(["127.0.0.1", "0.0.0.0", "[::]", "[::1]"]);

async function defaultEnumerateLocalPorts(): Promise<number[]> {
  try {
    const systemRoot = process.env.SystemRoot ?? "C:\\Windows";
    const netstat = path.join(systemRoot, "System32", "netstat.exe");
    const { stdout } = await execFileAsync(netstat, ["-ano", "-p", "tcp"], {
      timeout: 10_000,
      windowsHide: true,
      maxBuffer: 1024 * 1024,
    });
    const ports = new Set<number>();
    for (const rawLine of stdout.split(/\r?\n/)) {
      const line = rawLine.trim();
      if (!/^TCP\b/i.test(line)) {
        continue;
      }
      const fields = line.split(/\s+/);
      const local = fields[1];
      const foreign = fields[2];
      if (!local || !foreign || !listeningForeignAddresses.has(foreign)) {
        continue;
      }
      const pid = Number(fields[fields.length - 1]);
      if (!Number.isSafeInteger(pid) || pid <= 0) {
        continue;
      }
      const lastColon = local.lastIndexOf(":");
      const host = lastColon > 0 ? local.slice(0, lastColon) : "";
      const port = Number(lastColon >= 0 ? local.slice(lastColon + 1) : "");
      if (!localListenHosts.has(host)) {
        continue;
      }
      if (Number.isSafeInteger(port) && port > 0 && port <= 65535) {
        ports.add(port);
      }
    }
    return [...ports];
  } catch {
    return [];
  }
}

async function defaultIsLocalPortAvailable(port: number): Promise<boolean> {
  return await new Promise<boolean>((resolve) => {
    const server = createServer();
    let settled = false;
    const finish = (available: boolean): void => {
      if (settled) return;
      settled = true;
      resolve(available);
    };
    server.unref();
    server.once("error", () => finish(false));
    server.listen({ host: "127.0.0.1", port, exclusive: true }, () => {
      server.close((error) => finish(!error));
    });
  });
}

function isForegroundSession(session: OpenCodeSession, instanceCwd: string): boolean {
  if (session.parentID) {
    return false;
  }
  if (!session.directory || !instanceCwd) {
    return true;
  }
  return normalizePath(session.directory) === normalizePath(instanceCwd);
}

function selectMostRecentSession(
  sessions: OpenCodeSession[],
): OpenCodeSession | undefined {
  return [...sessions].sort(
    (left, right) => sessionUpdatedAt(right) - sessionUpdatedAt(left),
  )[0];
}

function sessionUpdatedAt(session: OpenCodeSession): number {
  const updated = session.time?.updated ?? session.time?.created ?? 0;
  return Number.isFinite(updated) ? updated : 0;
}

function normalizePath(value: string): string {
  return value.replace(/\\/g, "/").replace(/\/+$/, "").toLowerCase();
}
