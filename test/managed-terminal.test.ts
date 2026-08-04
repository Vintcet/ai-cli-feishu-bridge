import assert from "node:assert/strict";
import net from "node:net";
import path from "node:path";
import test from "node:test";

import type { SessionRecord } from "../src/domain.js";
import { ManagedTerminalRouter } from "../src/managed-terminal.js";

function session(terminalId: string, cwd: string, sessionId = "session-12345678"): SessionRecord {
  const now = new Date().toISOString();
  return {
    sessionId,
    shortId: "12345678",
    cwd,
    projectName: path.basename(cwd),
    status: "waiting",
    openedAt: now,
    lastSeenAt: now,
    managedTerminalId: terminalId,
  };
}

test("claims same-directory windows by explicit terminal id", () => {
  const router = new ManagedTerminalRouter();
  const cwd = path.resolve("same-project");
  router.register({ terminalId: "terminal111", cwd, elevated: false, ready: true });
  router.register({
    terminalId: "terminal222",
    cwd,
    elevated: true,
    ready: true,
    runtime: "claudecode",
  });

  const claim = router.claimById("terminal222", cwd, "session-second");
  assert.equal(claim?.terminalId, "terminal222");
  assert.equal(claim?.elevated, true);
  assert.equal(claim?.runtime, "claudecode");
  const registrations = router.listOnline();
  assert.equal(
    registrations.find((item) => item.terminalId === "terminal222")?.sessionId,
    "session-second",
  );
  assert.equal(
    registrations.find((item) => item.terminalId === "terminal111")?.sessionId,
    undefined,
  );
});

test("rejects remote input until the terminal is ready", async () => {
  const router = new ManagedTerminalRouter();
  const cwd = path.resolve("cold-start");
  router.register({ terminalId: "terminal333", cwd, elevated: false, ready: false });
  const target = session("terminal333", cwd);
  assert.equal(router.isReady(target), false);
  await assert.rejects(
    router.send(target, "hello"),
    /仍在启动/,
  );

  router.claimById("terminal333", cwd, target.sessionId);
  assert.equal(router.isReady(target), true);
});

test("cwd fallback only claims an available registration", () => {
  const router = new ManagedTerminalRouter();
  const cwd = path.resolve("legacy-fallback");
  router.register({ terminalId: "terminal444", cwd, elevated: false, ready: true });
  router.register({ terminalId: "terminal555", cwd, elevated: false, ready: true });

  assert.equal(router.claim(cwd, "session-one")?.terminalId, "terminal444");
  assert.equal(router.claim(cwd, "session-two")?.terminalId, "terminal555");
  assert.equal(router.claim(cwd, "session-three"), undefined);
  assert.throws(
    () => router.claimById("terminal555", path.resolve("other-project"), "session-two"),
    /项目目录不匹配/,
  );
});

test("rejects an invalid terminal submit mode before connecting", async () => {
  const router = new ManagedTerminalRouter();
  const cwd = path.resolve("invalid-submit-mode");
  router.register({ terminalId: "terminal666", cwd, elevated: false, ready: true });
  const target = session("terminal666", cwd);
  await assert.rejects(
    router.send(target, "hello", "invalid" as never),
    /提交模式无效/,
  );
});

test("queued input is rejected if the terminal changes session ownership", async () => {
  const router = new ManagedTerminalRouter();
  const cwd = path.resolve("ownership-race");
  const terminalId = "terminal777";
  router.register({ terminalId, cwd, elevated: false, ready: true });
  router.claimById(terminalId, cwd, "session-owner-one");

  let releaseFirst: (() => void) | undefined;
  const firstGate = new Promise<void>((resolve) => {
    releaseFirst = resolve;
  });
  let markFirstStarted: (() => void) | undefined;
  const firstStarted = new Promise<void>((resolve) => {
    markFirstStarted = resolve;
  });
  let sendOnceCalls = 0;
  const internal = router as unknown as {
    sendOnce: (...args: unknown[]) => Promise<void>;
  };
  internal.sendOnce = async () => {
    sendOnceCalls += 1;
    if (sendOnceCalls === 1) {
      markFirstStarted?.();
      await firstGate;
    }
  };

  const first = router.send(
    session(terminalId, cwd, "session-owner-one"),
    "first",
  );
  await firstStarted;
  const second = router.send(
    session(terminalId, cwd, "session-owner-one"),
    "second",
  );
  const secondRejected = assert.rejects(second, /目标会话不匹配/);

  router.release("session-owner-one");
  router.claimById(terminalId, cwd, "session-owner-two");
  releaseFirst?.();

  await first;
  await secondRejected;
  assert.equal(sendOnceCalls, 1);
});

test("retries while a managed terminal pipe is being recreated", async (context) => {
  if (process.platform !== "win32") {
    context.skip("Windows named pipe behavior");
    return;
  }
  const terminalId = "terminal" + Date.now();
  const cwd = path.resolve("pipe-retry");
  const router = new ManagedTerminalRouter();
  router.register({ terminalId, cwd, elevated: false, ready: true });
  const target = session(terminalId, cwd);
  const pipePath = "\\\\.\\pipe\\AiCliFeishu." + terminalId;
  const server = net.createServer((socket) => {
    socket.setEncoding("utf8");
    socket.once("data", () => socket.end('{"ok":true}\n'));
  });
  const listenTimer = setTimeout(() => server.listen(pipePath), 220);
  try {
    await router.send(target, "hello");
  } finally {
    clearTimeout(listenTimer);
    await new Promise<void>((resolve) => server.close(() => resolve()));
  }
});
