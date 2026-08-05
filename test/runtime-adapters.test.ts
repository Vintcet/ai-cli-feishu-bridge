import assert from "node:assert/strict";
import test from "node:test";

import type { SessionRecord } from "../src/domain.js";
import { ManagedTerminalRuntimeAdapter } from "../src/runtime-adapters/managed-terminal-runtime-adapter.js";
import { OpenCodeRuntimeAdapter } from "../src/runtime-adapters/opencode-runtime-adapter.js";

function session(runtime: SessionRecord["runtime"]): SessionRecord {
  return {
    sessionId: `${runtime}-session-1`,
    shortId: "session1",
    cwd: "K:\\project",
    projectName: "project",
    status: "ready",
    openedAt: "2026-08-05T10:00:00.000Z",
    lastSeenAt: "2026-08-05T10:00:00.000Z",
    runtime,
    managedTerminalId: runtime === "opencode" ? undefined : "terminal-1234",
  };
}

test("managed terminal adapters preserve runtime, prompt, and submit mode", async () => {
  const calls: unknown[][] = [];
  const terminals = {
    isReady: (current: SessionRecord) => current.status === "ready",
    send: async (
      current: SessionRecord,
      prompt: string,
      mode: "steer" | "queue" = "steer",
    ) => {
      calls.push([current, prompt, mode]);
    },
  };
  const codex = new ManagedTerminalRuntimeAdapter("codex", terminals);
  const claude = new ManagedTerminalRuntimeAdapter("claudecode", terminals);
  const current = session("claudecode");

  assert.equal(codex.runtime, "codex");
  assert.equal(claude.runtime, "claudecode");
  assert.equal(claude.isReady(current), true);
  assert.equal(claude.capabilities.has("prompt.queue"), true);
  await claude.sendPrompt(current, "不要改变这段消息", "queue");
  assert.deepEqual(calls, [[current, "不要改变这段消息", "queue"]]);
});

test("OpenCode adapter delegates readiness and live prompt sending", async () => {
  const calls: unknown[][] = [];
  const manager = {
    findActiveInstanceBySession: (sessionId: string) =>
      sessionId === "opencode-session-1" ? { port: 4096 } : undefined,
    sendPrompt: async (sessionId: string, prompt: string) => {
      calls.push([sessionId, prompt]);
    },
  };
  const adapter = new OpenCodeRuntimeAdapter(manager);
  const current = session("opencode");

  assert.equal(adapter.isReady(current), true);
  assert.equal(adapter.capabilities.has("prompt.send"), true);
  assert.equal(adapter.capabilities.has("prompt.queue"), false);
  await adapter.sendPrompt(current, "继续", "steer");
  assert.deepEqual(calls, [["opencode-session-1", "继续"]]);
  await assert.rejects(
    adapter.sendPrompt(current, "排队", "queue"),
    /不支持原生消息排队/u,
  );
});

test("OpenCode adapter fails clearly when support is disabled", async () => {
  const adapter = new OpenCodeRuntimeAdapter(undefined);
  const current = session("opencode");

  assert.equal(adapter.isReady(current), false);
  await assert.rejects(
    adapter.sendPrompt(current, "继续", "steer"),
    /opencode 支持未启用/u,
  );
});
