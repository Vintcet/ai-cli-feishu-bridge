import { execFile } from "node:child_process";
import { createServer } from "node:net";
import path from "node:path";
import { promisify } from "node:util";

import { OpenCodeClient } from "./opencode-client.js";
import type {
  OpenCodeEventHandlers,
  OpenCodePermissionResponse,
  OpenCodeSession,
} from "./opencode-client.js";

const execFileAsync = promisify(execFile);

export interface OpenCodeInstance {
  port: number;
  cwd: string;
  client: OpenCodeClient;
  connectedAt: string;
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

export class OpenCodeManager {
  private readonly instances = new Map<number, OpenCodeInstance>();
  private readonly pendingPorts = new Set<number>();
  private readonly pendingSessionIds = new Map<number, string>();
  private readonly retryTimers = new Map<number, ReturnType<typeof setTimeout>>();
  private readonly sessionPorts = new Map<string, number>();
  private readonly connectingPorts = new Set<number>();
  private readonly discoveryMisses = new Map<number, number>();
  private readonly basePort: number;
  private readonly maxPort: number;
  private readonly autoDiscover: boolean;
  private readonly scanIntervalMs: number;
  private readonly enumerateLocalPorts: () => Promise<number[]>;
  private readonly isLocalPortAvailable: (port: number) => Promise<boolean>;
  private discoverTimer: ReturnType<typeof setTimeout> | undefined;
  private discoverRunning = false;

  constructor(
    private readonly handlers: OpenCodeManagerHandlers,
    options: {
      basePort?: number;
      maxPort?: number;
      autoDiscover?: boolean;
      scanIntervalMs?: number;
      enumerateLocalPorts?: () => Promise<number[]>;
      isLocalPortAvailable?: (port: number) => Promise<boolean>;
    } = {},
  ) {
    this.basePort = options.basePort ?? 5100;
    this.maxPort = options.maxPort ?? 5999;
    this.autoDiscover = options.autoDiscover ?? true;
    this.scanIntervalMs = options.scanIntervalMs ?? 20_000;
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

  async launch(cwd: string, sessionId?: string): Promise<OpenCodeLaunchResult> {
    const port = await this.allocatePort();
    this.pendingPorts.add(port);
    if (sessionId) {
      this.pendingSessionIds.set(port, sessionId);
    }
    void this.retryConnect(port, cwd, 0);
    return { port };
  }

  async register(port: number, cwd: string): Promise<void> {
    this.pendingPorts.add(port);
    await this.connect(port, cwd);
  }

  async unregister(port: number): Promise<void> {
    const instance = this.instances.get(port);
    if (instance) {
      instance.closeSubscription();
      this.instances.delete(port);
      for (const [sessionId, sessionPort] of this.sessionPorts) {
        if (sessionPort === port) {
          this.sessionPorts.delete(sessionId);
        }
      }
    }
    this.pendingPorts.delete(port);
    const timer = this.retryTimers.get(port);
    if (timer) {
      clearTimeout(timer);
      this.retryTimers.delete(port);
    }
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

  private async connect(port: number, cwd: string): Promise<void> {
    const client = new OpenCodeClient(`http://127.0.0.1:${port}`);
    const healthy = await client.health();
    if (!healthy) {
      throw new Error(`opencode 端口 ${port} 未就绪`);
    }
    const wrappedHandlers: OpenCodeEventHandlers = {
      ...this.handlers.eventHandlers,
      onSessionCreated: (session) => {
        this.rememberSession(port, session.id);
        this.handlers.eventHandlers.onSessionCreated?.(session);
      },
      onSessionDeleted: (sessionId) => {
        this.forgetSession(sessionId);
        this.handlers.eventHandlers.onSessionDeleted?.(sessionId);
      },
      onDisconnected: () => {
        void this.handleInstanceSubscriptionClosed(port).catch((error) => {
          console.warn(`[opencode] 端口 ${port} 订阅结束后处理失败：`, error);
        });
      },
    };
    const { close: closeSubscription } = client.subscribe(wrappedHandlers);
    const instance: OpenCodeInstance = {
      port,
      cwd,
      client,
      connectedAt: new Date().toISOString(),
      closeSubscription,
    };
    this.instances.set(port, instance);
    this.pendingPorts.delete(port);
    void this.seedSessions(instance);
    const pendingSessionId = this.pendingSessionIds.get(port);
    if (pendingSessionId) {
      this.pendingSessionIds.delete(port);
      void this.seedPendingSession(instance, pendingSessionId);
    }
    this.handlers.onInstanceConnected(port, cwd);
  }

  private async seedSessions(instance: OpenCodeInstance): Promise<void> {
    try {
      const sessions = await instance.client.listSessions();
      for (const session of sessions) {
        if (!isSessionWithinProject(session, instance.cwd)) {
          continue;
        }
        this.rememberSession(instance.port, session.id);
        this.handlers.eventHandlers.onSessionCreated?.(session);
      }
    } catch (error) {
      console.warn(`[opencode] 端口 ${instance.port} 同步会话失败：`, error);
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
      if (this.sessionPorts.get(session.id) === instance.port) {
        return;
      }
      this.rememberSession(instance.port, session.id);
      this.handlers.eventHandlers.onSessionCreated?.(session);
      console.log(
        `[opencode] 端口 ${instance.port} 登记恢复的会话 #${session.id}。`,
      );
    } catch (error) {
      console.warn(`[opencode] 端口 ${instance.port} 登记恢复会话失败：`, error);
    }
  }

  rememberSession(port: number, sessionId: string): void {
    this.sessionPorts.set(sessionId, port);
  }

  forgetSession(sessionId: string): void {
    this.sessionPorts.delete(sessionId);
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
    const instance = this.findInstanceBySession(sessionId);
    if (!instance) {
      throw new Error("找不到对应的 opencode 实例");
    }
    await instance.client.replyPermission(sessionId, permissionId, response);
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
    if (!this.autoDiscover || this.discoverTimer) {
      return;
    }
    const schedule = (): void => {
      this.discoverTimer = setTimeout(() => {
        this.discoverTimer = undefined;
        void this.runDiscoveryPass().finally(schedule);
      }, this.scanIntervalMs);
      this.discoverTimer.unref?.();
    };
    schedule();
  }

  stopAutoDiscovery(): void {
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

  private async handleInstanceSubscriptionClosed(port: number): Promise<void> {
    const instance = this.instances.get(port);
    if (!instance) {
      return;
    }
    if (this.connectingPorts.has(port)) {
      return;
    }
    this.connectingPorts.add(port);
    try {
      const healthy = await instance.client.probeHealth();
      if (healthy.healthy) {
        await this.connect(port, instance.cwd);
      } else {
        console.warn(`[opencode] 端口 ${port} 订阅已断开且服务不可达，自动移除。`);
        await this.unregister(port);
      }
    } catch {
      await this.unregister(port);
    } finally {
      this.connectingPorts.delete(port);
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

function isSessionWithinProject(
  session: OpenCodeSession,
  instanceCwd: string,
): boolean {
  if (!session.directory || !instanceCwd) {
    return true;
  }
  const child = normalizePath(session.directory);
  const parent = normalizePath(instanceCwd);
  return child === parent || child.startsWith(`${parent}/`);
}

function normalizePath(value: string): string {
  return value.replace(/\\/g, "/").replace(/\/+$/, "").toLowerCase();
}
