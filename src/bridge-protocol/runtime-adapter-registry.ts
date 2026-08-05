import type { RuntimeName, SessionRecord } from "../domain.js";
import type { RuntimeAdapter } from "./runtime-adapter.js";
import type { RuntimeCapability } from "./runtime-capabilities.js";

export class RuntimeAdapterRegistry {
  private readonly adapters = new Map<RuntimeName, RuntimeAdapter>();

  register(adapter: RuntimeAdapter): void {
    if (this.adapters.has(adapter.runtime)) {
      throw new Error(`运行时 ${adapter.runtime} 已注册 Adapter。`);
    }
    this.adapters.set(adapter.runtime, adapter);
  }

  forRuntime(runtime: RuntimeName = "codex"): RuntimeAdapter {
    const adapter = this.adapters.get(runtime);
    if (!adapter) {
      throw new Error(`运行时 ${runtime} 未注册 Adapter。`);
    }
    return adapter;
  }

  forSession(session: Pick<SessionRecord, "runtime">): RuntimeAdapter {
    return this.forRuntime(session.runtime ?? "codex");
  }

  requireCapability(
    runtime: RuntimeName,
    capability: RuntimeCapability,
  ): RuntimeAdapter {
    const adapter = this.forRuntime(runtime);
    if (!adapter.capabilities.has(capability)) {
      throw new Error(`运行时 ${runtime} 不支持能力 ${capability}。`);
    }
    return adapter;
  }
}
