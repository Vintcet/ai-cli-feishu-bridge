import assert from "node:assert/strict";
import test from "node:test";

import type { RuntimeAdapter } from "../src/bridge-protocol/runtime-adapter.js";
import { RuntimeAdapterRegistry } from "../src/bridge-protocol/runtime-adapter-registry.js";
import type { RuntimeName, SessionRecord } from "../src/domain.js";

function fakeAdapter(runtime: RuntimeName): RuntimeAdapter {
  return {
    runtime,
    capabilities: new Set(["prompt.send"]),
    isReady: () => true,
    sendPrompt: async () => {},
  };
}

test("resolves all supported runtimes and defaults legacy sessions to Codex", () => {
  const registry = new RuntimeAdapterRegistry();
  const codex = fakeAdapter("codex");
  const claude = fakeAdapter("claudecode");
  const opencode = fakeAdapter("opencode");
  registry.register(codex);
  registry.register(claude);
  registry.register(opencode);

  assert.equal(registry.forRuntime("codex"), codex);
  assert.equal(registry.forRuntime("claudecode"), claude);
  assert.equal(registry.forRuntime("opencode"), opencode);
  assert.equal(registry.forSession({}), codex);
});

test("reports duplicate registrations, missing runtimes, and capabilities", () => {
  const registry = new RuntimeAdapterRegistry();
  registry.register(fakeAdapter("codex"));

  assert.throws(
    () => registry.register(fakeAdapter("codex")),
    /已注册 Adapter/u,
  );
  assert.throws(
    () => registry.forRuntime("opencode"),
    /未注册 Adapter/u,
  );
  assert.throws(
    () => registry.requireCapability("codex", "approval.resolve"),
    /不支持能力 approval\.resolve/u,
  );
});
