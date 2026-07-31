import assert from "node:assert/strict";
import test from "node:test";

import { startHookHttpServer, type HookHttpHandlers } from "../src/http-server.js";

function handlers(): HookHttpHandlers {
  return {
    health: () => ({ ok: true }),
    managedTerminalRegister: () => ({ ok: true }),
    managedTerminalUnregister: async () => ({ ok: true }),
    sessionAlias: async () => ({ ok: true }),
    sessionHistoryHide: async (payload) => ({ ok: true, sessionId: payload.sessionId }),
    localApproval: async (payload) => ({ ok: true, requestId: payload.requestId }),
    settingsUpdate: async (payload) => ({ ok: true, settings: payload }),
    permission: async () => ({}),
    requestUserInput: async () => ({}),
    activity: async () => ({}),
    sessionStart: async () => ({}),
    sessionEnd: async () => ({}),
    stop: async () => ({}),
  };
}

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
        "x-codex-feishu-control-token": token,
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
      body: JSON.stringify({ autoApprove: true }),
    });
    assert.equal(unauthorized.status, 401);
    const authorized = await fetch(url, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-codex-feishu-control-token": token,
      },
      body: JSON.stringify({ autoApprove: true }),
    });
    assert.equal(authorized.status, 200);
    const body = await authorized.json() as { settings?: { autoApprove?: boolean } };
    assert.equal(body.settings?.autoApprove, true);
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
        "x-codex-feishu-control-token": token,
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
