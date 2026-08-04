import { OpenCodeClient } from "./opencode-client.js";

interface DiscoverableOpenCodeInstance {
  port: number;
  client: OpenCodeClient;
}

interface OpenCodeDiscoveryCoordinatorDependencies {
  enabled: boolean;
  scanIntervalMs: number;
  misses: Map<number, number>;
  enumerateLocalPorts: () => Promise<number[]>;
  knownPorts: () => ReadonlySet<number>;
  listInstances: () => DiscoverableOpenCodeInstance[];
  isCurrentClient: (port: number, client: OpenCodeClient) => boolean;
  tryBeginConnection: (port: number) => boolean;
  endConnection: (port: number) => void;
  connect: (port: number, cwd: string) => Promise<void>;
  unregister: (port: number) => Promise<void>;
}

export class OpenCodeDiscoveryCoordinator {
  private timer: ReturnType<typeof setTimeout> | undefined;
  private running = false;
  private active = false;

  constructor(
    private readonly dependencies: OpenCodeDiscoveryCoordinatorDependencies,
  ) {}

  start(): void {
    if (!this.dependencies.enabled || this.active) {
      return;
    }
    this.active = true;
    this.schedule();
  }

  stop(): void {
    this.active = false;
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = undefined;
    }
  }

  async runPass(): Promise<void> {
    if (this.running) {
      return;
    }
    this.running = true;
    try {
      const ports = await this.dependencies.enumerateLocalPorts();
      const knownPorts = this.dependencies.knownPorts();
      for (const port of ports) {
        if (
          knownPorts.has(port) ||
          !this.dependencies.tryBeginConnection(port)
        ) {
          continue;
        }
        void this.connectDiscovered(port).finally(() => {
          this.dependencies.endConnection(port);
        });
      }

      for (const instance of this.dependencies.listInstances()) {
        const healthy = await instance.client.probeHealth();
        if (
          !this.dependencies.isCurrentClient(instance.port, instance.client)
        ) {
          // The port reconnected while the old probe was in flight. Its result
          // must not remove or penalize the replacement client.
          this.dependencies.misses.delete(instance.port);
          continue;
        }
        if (healthy.healthy) {
          this.dependencies.misses.delete(instance.port);
          continue;
        }
        const misses = (this.dependencies.misses.get(instance.port) ?? 0) + 1;
        if (misses >= 3) {
          this.dependencies.misses.delete(instance.port);
          console.warn(
            `[opencode] 端口 ${instance.port} 已停止响应，自动移除。`,
          );
          await this.dependencies.unregister(instance.port);
        } else {
          this.dependencies.misses.set(instance.port, misses);
        }
      }
    } catch (error) {
      console.warn("[opencode] 自动发现一轮扫描失败：", error);
    } finally {
      this.running = false;
    }
  }

  private schedule(): void {
    if (!this.active || this.timer) {
      return;
    }
    this.timer = setTimeout(() => {
      this.timer = undefined;
      if (!this.active) {
        return;
      }
      void this.runPass().finally(() => {
        if (this.active) {
          this.schedule();
        }
      });
    }, this.dependencies.scanIntervalMs);
    this.timer.unref?.();
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
      await this.dependencies.connect(port, cwd);
      console.log(`[opencode] 自动发现并连接端口 ${port} 的 opencode 实例。`);
    } catch {
      // 失败时留到下一轮扫描重试。
    }
  }
}
