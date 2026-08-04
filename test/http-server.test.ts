import assert from "node:assert/strict";
import test from "node:test";

import { startHookHttpServer, type HookHttpHandlers } from "../src/http-server.js";

function handlers(): HookHttpHandlers {
  return {
    health: () => ({ ok: true }),
    shutdown: () => {},
    managedTerminalRegister: () => ({ ok: true }),
    managedTerminalUnregister: async () => ({ ok: true }),
    sessionAlias: async () => ({ ok: true }),
    sessionHistoryHide: async (payload) => ({ ok: true, sessionId: payload.sessionId }),
    runtimeLaunchClaim: () => ({
      ok: true,
      request: {
        requestId: "launch-1",
        sessionId: "session-1",
        runtime: "codex",
        cwd: "C:/demo",
        elevated: false,
      },
    }),
    runtimeLaunchComplete: async (payload) => ({
      ok: true,
      requestId: payload.requestId,
    }),
    localApproval: async (payload) => ({ ok: true, requestId: payload.requestId }),
    settingsUpdate: async (payload) => ({ ok: true, settings: payload }),
    permission: async () => ({}),
    requestUserInput: async () => ({}),
    activity: async () => ({}),
    sessionStart: async () => ({}),
    sessionEnd: async () => ({}),
    stop: async () => ({}),
    opencodeLaunch: async (payload) => ({ ok: true, port: 5100, cwd: payload.cwd }),
    opencodeRegister: async (payload) => ({ ok: true, port: payload.port }),
    opencodeUnregister: async (payload) => ({ ok: true, port: payload.port }),
  };
}

test("shutdown endpoint requires the control token and responds before stopping", async () => {
  const token = "f".repeat(64);
  let shutdownCalls = 0;
  let resolveShutdown!: () => void;
  const shutdownCalled = new Promise<void>((resolve) => {
    resolveShutdown = resolve;
  });
  const custom = handlers();
  custom.shutdown = () => {
    shutdownCalls += 1;
    resolveShutdown();
  };
  const server = startHookHttpServer("127.0.0.1", 0, custom, token);
  try {
    await new Promise<void>((resolve, reject) => {
      server.once("listening", resolve);
      server.once("error", reject);
    });
    const address = server.address();
    assert.ok(address && typeof address === "object");
    const url = `http://127.0.0.1:${address.port}/control/shutdown`;

    const unauthorized = await fetch(url, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: "{}",
    });
    assert.equal(unauthorized.status, 401);
    assert.equal(shutdownCalls, 0);

    const authorized = await fetch(url, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-ai-cli-feishu-control-token": token,
      },
      body: "{}",
    });
    assert.equal(authorized.status, 202);
    assert.deepEqual(await authorized.json(), { ok: true });
    await shutdownCalled;
    assert.equal(shutdownCalls, 1);
  } finally {
    await new Promise<void>((resolve, reject) => {
      server.close((error) => error ? reject(error) : resolve());
    });
  }
});

test("local approval endpoint requires the persistent control token", async () => {
  const token = "a".repeat(64);
  const server = startHookHttpServer("127.0.0.1", 0, handlers(), token);
  try {
    await new Promise<void>((resolve, reject) => {
      server.once("listening", resolve);
      server.once("error", reject);
    });
    const address = server.address();
    assert.ok(address && typeof address === "object");
    const url = `http://127.0.0.1:${address.port}/approvals/resolve`;

    const unauthorized = await fetch(url, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ requestId: "request-1", resolution: "allow" }),
    });
    assert.equal(unauthorized.status, 401);

    const authorized = await fetch(url, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-ai-cli-feishu-control-token": token,
      },
      body: JSON.stringify({ requestId: "request-1", resolution: "allow" }),
    });
    assert.equal(authorized.status, 200);
    assert.equal((await authorized.json() as { requestId?: string }).requestId, "request-1");
  } finally {
    await new Promise<void>((resolve, reject) => {
      server.close((error) => error ? reject(error) : resolve());
    });
  }
});

test("settings endpoint requires the persistent control token", async () => {
  const token = "b".repeat(64);
  const server = startHookHttpServer("127.0.0.1", 0, handlers(), token);
  try {
    await new Promise<void>((resolve, reject) => {
      server.once("listening", resolve);
      server.once("error", reject);
    });
    const address = server.address();
    assert.ok(address && typeof address === "object");
    const url = `http://127.0.0.1:${address.port}/settings`;
    const unauthorized = await fetch(url, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ autoApprove: true, notifyAutoApprovals: true }),
    });
    assert.equal(unauthorized.status, 401);
    const authorized = await fetch(url, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-ai-cli-feishu-control-token": token,
      },
      body: JSON.stringify({ autoApprove: true, notifyAutoApprovals: true }),
    });
    assert.equal(authorized.status, 200);
    const body = await authorized.json() as {
      settings?: { autoApprove?: boolean; notifyAutoApprovals?: boolean };
    };
    assert.equal(body.settings?.autoApprove, true);
    assert.equal(body.settings?.notifyAutoApprovals, true);
  } finally {
    await new Promise<void>((resolve, reject) => {
      server.close((error) => error ? reject(error) : resolve());
    });
  }
});

test("history hide endpoint requires the persistent control token", async () => {
  const token = "c".repeat(64);
  const server = startHookHttpServer("127.0.0.1", 0, handlers(), token);
  try {
    await new Promise<void>((resolve, reject) => {
      server.once("listening", resolve);
      server.once("error", reject);
    });
    const address = server.address();
    assert.ok(address && typeof address === "object");
    const url = `http://127.0.0.1:${address.port}/sessions/history/hide`;
    const unauthorized = await fetch(url, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ sessionId: "session-1" }),
    });
    assert.equal(unauthorized.status, 401);
    const authorized = await fetch(url, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-ai-cli-feishu-control-token": token,
      },
      body: JSON.stringify({ sessionId: "session-1" }),
    });
    assert.equal(authorized.status, 200);
    assert.equal(
      (await authorized.json() as { sessionId?: string }).sessionId,
      "session-1",
    );
  } finally {
    await new Promise<void>((resolve, reject) => {
      server.close((error) => error ? reject(error) : resolve());
    });
  }
});

test("runtime launch endpoints require the persistent control token", async () => {
  const token = "e".repeat(64);
  const server = startHookHttpServer("127.0.0.1", 0, handlers(), token);
  try {
    await new Promise<void>((resolve, reject) => {
      server.once("listening", resolve);
      server.once("error", reject);
    });
    const address = server.address();
    assert.ok(address && typeof address === "object");
    const base = `http://127.0.0.1:${address.port}`;
    const unauthorized = await fetch(`${base}/runtime-launches/claim`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: "{}",
    });
    assert.equal(unauthorized.status, 401);
    const authorized = await fetch(`${base}/runtime-launches/claim`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-ai-cli-feishu-control-token": token,
      },
      body: "{}",
    });
    assert.equal(authorized.status, 200);
    const claim = await authorized.json() as {
      request?: { requestId?: string; cwd?: string };
    };
    assert.equal(claim.request?.requestId, "launch-1");
    assert.equal(claim.request?.cwd, "C:/demo");

    const completed = await fetch(`${base}/runtime-launches/complete`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-ai-cli-feishu-control-token": token,
      },
      body: JSON.stringify({ requestId: "launch-1", success: true }),
    });
    assert.equal(completed.status, 200);
    assert.equal(
      (await completed.json() as { requestId?: string }).requestId,
      "launch-1",
    );
  } finally {
    await new Promise<void>((resolve, reject) => {
      server.close((error) => error ? reject(error) : resolve());
    });
  }
});

test("opencode control endpoints require the persistent control token", async () => {
  const token = "d".repeat(64);
  const server = startHookHttpServer("127.0.0.1", 0, handlers(), token);
  try {
    await new Promise<void>((resolve, reject) => {
      server.once("listening", resolve);
      server.once("error", reject);
    });
    const address = server.address();
    assert.ok(address && typeof address === "object");
    const base = `http://127.0.0.1:${address.port}`;

    const unauthorizedLaunch = await fetch(`${base}/opencode/launch`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ cwd: "C:/demo" }),
    });
    assert.equal(unauthorizedLaunch.status, 401);

    const authorizedLaunch = await fetch(`${base}/opencode/launch`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-ai-cli-feishu-control-token": token,
      },
      body: JSON.stringify({ cwd: "C:/demo" }),
    });
    assert.equal(authorizedLaunch.status, 200);
    assert.equal((await authorizedLaunch.json() as { port?: number }).port, 5100);

    const unauthorizedRegister = await fetch(`${base}/opencode/register`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ port: 5101, cwd: "C:/demo" }),
    });
    assert.equal(unauthorizedRegister.status, 401);

    const authorizedRegister = await fetch(`${base}/opencode/register`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-ai-cli-feishu-control-token": token,
      },
      body: JSON.stringify({ port: 5101, cwd: "C:/demo" }),
    });
    assert.equal(authorizedRegister.status, 200);
    assert.equal((await authorizedRegister.json() as { port?: number }).port, 5101);

    const authorizedUnregister = await fetch(`${base}/opencode/unregister`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-ai-cli-feishu-control-token": token,
      },
      body: JSON.stringify({ port: 5101 }),
    });
    assert.equal(authorizedUnregister.status, 200);
  } finally {
    await new Promise<void>((resolve, reject) => {
      server.close((error) => error ? reject(error) : resolve());
    });
  }
});

test("health returns only liveness data to unauthenticated callers", async () => {
  const token = "c".repeat(64);
  const custom = handlers();
  const refreshCalls: boolean[] = [];
  custom.health = (includeLocalSecrets, forceRefresh) => {
    refreshCalls.push(forceRefresh);
    return includeLocalSecrets
      ? {
          ok: true,
          pairingCode: "SECRET1234",
          bindingCommand: "绑定 SECRET1234",
          activeSessions: 2,
        }
      : { ok: true };
  };
  const server = startHookHttpServer("127.0.0.1", 0, custom, token);
  try {
    await new Promise<void>((resolve, reject) => {
      server.once("listening", resolve);
      server.once("error", reject);
    });
    const address = server.address();
    assert.ok(address && typeof address === "object");
    const base = `http://127.0.0.1:${address.port}`;

    const anonymous = await fetch(`${base}/health?refresh=1`);
    assert.equal(anonymous.status, 200);
    const anonymousBody = await anonymous.json() as {
      pairingCode?: string;
      bindingCommand?: string;
      activeSessions?: number;
    };
    assert.equal(anonymousBody.pairingCode, undefined);
    assert.equal(anonymousBody.bindingCommand, undefined);
    assert.equal(anonymousBody.activeSessions, undefined);

    const authorized = await fetch(`${base}/health?refresh=1`, {
      headers: { "x-ai-cli-feishu-control-token": token },
    });
    const authorizedBody = await authorized.json() as { pairingCode?: string };
    assert.equal(authorizedBody.pairingCode, "SECRET1234");
    assert.deepEqual(refreshCalls, [false, true]);
  } finally {
    await new Promise<void>((resolve, reject) => {
      server.close((error) => error ? reject(error) : resolve());
    });
  }
});

test("hook posts require JSON, same-site metadata, and the persistent token", async () => {
  const token = "d".repeat(64);
  let stopCalls = 0;
  const custom = handlers();
  custom.stop = async () => {
    stopCalls += 1;
    return {};
  };
  const server = startHookHttpServer("127.0.0.1", 0, custom, token);
  try {
    await new Promise<void>((resolve, reject) => {
      server.once("listening", resolve);
      server.once("error", reject);
    });
    const address = server.address();
    assert.ok(address && typeof address === "object");
    const base = `http://127.0.0.1:${address.port}`;
    const payload = {
      hook_event_name: "Stop",
      session_id: "session-guard-1",
      turn_id: "turn-1",
      cwd: "C:/demo",
      model: "gpt-5",
      last_assistant_message: "done",
    };

    // text/plain is on the CORS safelist, so it must not reach a handler.
    const plain = await fetch(`${base}/hooks/stop`, {
      method: "POST",
      headers: { "content-type": "text/plain" },
      body: JSON.stringify(payload),
    });
    assert.equal(plain.status, 415);

    const crossSite = await fetch(`${base}/hooks/stop`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "sec-fetch-site": "cross-site",
      },
      body: JSON.stringify(payload),
    });
    assert.equal(crossSite.status, 403);
    assert.equal(stopCalls, 0);

    const unauthenticated = await fetch(`${base}/hooks/stop`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(payload),
    });
    assert.equal(unauthenticated.status, 401);
    assert.equal(stopCalls, 0);

    const legitimate = await fetch(`${base}/hooks/stop`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-ai-cli-feishu-control-token": token,
      },
      body: JSON.stringify(payload),
    });
    assert.equal(legitimate.status, 200);
    assert.equal(stopCalls, 1);
  } finally {
    await new Promise<void>((resolve, reject) => {
      server.close((error) => error ? reject(error) : resolve());
    });
  }
});
