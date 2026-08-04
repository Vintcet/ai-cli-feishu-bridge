import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";

import type { Binding } from "../src/domain.js";
import { BridgeStore } from "../src/store.js";

async function temporaryDirectory(): Promise<string> {
  return mkdtemp(path.join(os.tmpdir(), "codex-feishu-store-"));
}

function binding(openId: string, boundAt = new Date().toISOString()): Binding {
  return { openId, chatId: `chat-${openId}`, chatType: "p2p", boundAt };
}

test("migrates legacy bindings to one persistent owner", async () => {
  const directory = await temporaryDirectory();
  try {
    await writeFile(
      path.join(directory, "bindings.json"),
      JSON.stringify({
        users: {
          later: binding("later", "2026-01-02T00:00:00.000Z"),
          first: binding("first", "2026-01-01T00:00:00.000Z"),
        },
      }),
      "utf8",
    );
    const store = new BridgeStore(directory);
    await store.init();

    assert.equal(store.getOwnerOpenId(), "first");
    assert.deepEqual(store.listBindings().map((item) => item.openId), ["first"]);
    const persisted = JSON.parse(
      await readFile(path.join(directory, "bindings.json"), "utf8"),
    ) as { ownerOpenId?: string; users: Record<string, Binding> };
    assert.equal(persisted.ownerOpenId, "first");
    assert.deepEqual(Object.keys(persisted.users), ["first"]);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("requires the random code and rejects a second owner", async () => {
  const directory = await temporaryDirectory();
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const pairingCode = store.getPairingCode();
    assert.ok(pairingCode);

    assert.equal(await store.bindOwner(binding("owner"), "WRONG"), "invalid_code");
    assert.equal(store.getOwnerOpenId(), undefined);
    assert.equal(await store.bindOwner(binding("owner"), pairingCode), "bound");
    assert.equal(await store.bindOwner(binding("intruder"), pairingCode), "owner_mismatch");
    assert.equal(store.isBound("owner"), true);
    assert.equal(store.isBound("intruder"), false);

    assert.equal(await store.removeBinding("owner"), true);
    assert.equal(store.isBound("owner"), false);
    assert.equal(store.getOwnerOpenId(), "owner");
    assert.equal(store.getPairingCode(), undefined);
    assert.equal(await store.bindOwner(binding("owner"), undefined), "rebound");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("claims each inbound message id once and persists the claim", async () => {
  const directory = await temporaryDirectory();
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const results = await Promise.all(
      Array.from({ length: 8 }, () => store.claimInboundMessage("message-1")),
    );
    assert.equal(results.filter(Boolean).length, 1);

    const restarted = new BridgeStore(directory);
    await restarted.init();
    assert.equal(await restarted.claimInboundMessage("message-1"), false);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("creates one persistent local control token", async () => {
  const directory = await temporaryDirectory();
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const first = await store.getOrCreateControlToken();
    const second = await store.getOrCreateControlToken();
    assert.match(first, /^[a-f0-9]{64}$/);
    assert.equal(second, first);

    const restarted = new BridgeStore(directory);
    await restarted.init();
    assert.equal(await restarted.getOrCreateControlToken(), first);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("persists notification and automation settings", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-settings-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    assert.deepEqual(store.getSettings(), {
      workspaceRoot: "",
      notifyActivity: false,
      notifyUserPrompts: false,
      autoRetryErrors: false,
      retryMaxAttempts: 3,
      retryIntervalSeconds: 5,
      retryJitterSeconds: 3,
      autoApprove: false,
      notifyAutoApprovals: false,
    });
    await store.updateSettings({
      workspaceRoot: directory,
      notifyActivity: true,
      notifyUserPrompts: true,
      autoRetryErrors: true,
      retryMaxAttempts: 7,
      retryIntervalSeconds: 12,
      retryJitterSeconds: 4,
      autoApprove: true,
      notifyAutoApprovals: true,
    });
    const reopened = new BridgeStore(directory);
    await reopened.init();
    assert.deepEqual(reopened.getSettings(), {
      workspaceRoot: path.resolve(directory),
      notifyActivity: true,
      notifyUserPrompts: true,
      autoRetryErrors: true,
      retryMaxAttempts: 7,
      retryIntervalSeconds: 12,
      retryJitterSeconds: 4,
      autoApprove: true,
      notifyAutoApprovals: true,
    });
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("explicit null clears stale managed terminal metadata", async () => {
  const directory = await temporaryDirectory();
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const first = await store.upsertSession({
      sessionId: "session-clear-terminal",
      cwd: directory,
      status: "waiting",
      managedTerminalId: "terminal123",
      managedTerminalElevated: true,
    });
    assert.equal(first.managedTerminalId, "terminal123");

    const cleared = await store.upsertSession({
      sessionId: first.sessionId,
      cwd: directory,
      status: "running",
      managedTerminalId: null,
      managedTerminalElevated: null,
    });
    assert.equal(cleared.managedTerminalId, undefined);
    assert.equal(cleared.managedTerminalElevated, undefined);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("migrates legacy managed sessions into assistant history", async () => {
  const directory = await temporaryDirectory();
  try {
    const managedSessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    const externalSessionId = "019fb4e7-d831-7dd3-9745-42f85a8209bb";
    const openedAt = "2026-01-01T00:00:00.000Z";
    await writeFile(
      path.join(directory, "sessions.json"),
      JSON.stringify({
        sessions: {
          [managedSessionId]: {
            sessionId: managedSessionId,
            shortId: "9b45397b",
            cwd: directory,
            projectName: "managed",
            status: "ended",
            openedAt,
            lastSeenAt: openedAt,
            endedAt: openedAt,
            managedTerminalId: "terminal-legacy",
          },
          [externalSessionId]: {
            sessionId: externalSessionId,
            shortId: "5a8209bb",
            cwd: directory,
            projectName: "external",
            status: "ended",
            openedAt,
            lastSeenAt: openedAt,
            endedAt: openedAt,
          },
        },
      }),
      "utf8",
    );

    const store = new BridgeStore(directory, {
      endedSessionRetentionMs: 10 * 365 * 24 * 60 * 60 * 1000,
    });
    await store.init();
    assert.equal(store.getSession(managedSessionId)?.managedByAssistant, true);
    assert.deepEqual(
      store.listAssistantManagedSessions().map((session) => session.sessionId),
      [managedSessionId],
    );

    const reopened = new BridgeStore(directory, {
      endedSessionRetentionMs: 10 * 365 * 24 * 60 * 60 * 1000,
    });
    await reopened.init();
    assert.equal(reopened.getSession(managedSessionId)?.managedByAssistant, true);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("reopening an ended session records a new openedAt", async () => {
  const directory = await temporaryDirectory();
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const originalOpenedAt = "2020-01-01T00:00:00.000Z";
    const first = await store.upsertSession({
      sessionId: "session-reopened",
      cwd: directory,
      status: "waiting",
      openedAt: originalOpenedAt,
    });
    await store.upsertSession({
      sessionId: first.sessionId,
      cwd: directory,
      status: "ended",
    });
    const reopened = await store.upsertSession({
      sessionId: first.sessionId,
      cwd: directory,
      status: "running",
    });
    assert.notEqual(reopened.openedAt, originalOpenedAt);
    assert.ok(Date.parse(reopened.openedAt) > Date.parse(originalOpenedAt));
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("hides assistant history persistently without deleting the session", async () => {
  const directory = await temporaryDirectory();
  let store: BridgeStore | undefined;
  let reopened: BridgeStore | undefined;
  try {
    const sessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    store = new BridgeStore(directory);
    await store.init();
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "ended",
      managedByAssistant: true,
    });
    await store.upsertSession({
      sessionId: "external-session",
      cwd: directory,
      status: "ended",
    });

    assert.equal(store.listAssistantManagedSessions().length, 1);
    const hidden = await store.hideSessionFromHistory(sessionId);
    assert.ok(hidden?.historyHiddenAt);
    assert.equal(store.listAssistantManagedSessions().length, 0);
    assert.equal(store.getSession(sessionId)?.sessionId, sessionId);
    assert.equal(await store.hideSessionFromHistory("external-session"), undefined);

    reopened = new BridgeStore(directory);
    await reopened.init();
    assert.equal(reopened.listAssistantManagedSessions().length, 0);
    assert.ok(reopened.getSession(sessionId)?.historyHiddenAt);
    await reopened.upsertSession({
      sessionId,
      cwd: directory,
      status: "running",
    });
    assert.equal(reopened.listAssistantManagedSessions().length, 1);
    assert.equal(reopened.getSession(sessionId)?.historyHiddenAt, undefined);
    await reopened.upsertSession({
      sessionId,
      cwd: directory,
      status: "ended",
    });
    assert.equal(reopened.listAssistantManagedSessions().length, 1);
  } finally {
    await Promise.allSettled([store?.close(), reopened?.close()]);
    await rm(directory, { recursive: true, force: true });
  }
});

test("init repairs externally tracked sessions with stale assistant metadata", async () => {
  const directory = await temporaryDirectory();
  try {
    const sessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    const timestamp = "2026-01-01T00:00:00.000Z";
    await writeFile(
      path.join(directory, "sessions.json"),
      JSON.stringify({
        sessions: {
          [sessionId]: {
            sessionId,
            shortId: "9b45397b",
            cwd: directory,
            projectName: "external",
            status: "waiting",
            openedAt: timestamp,
            lastSeenAt: timestamp,
            clientProcessId: process.pid,
            managedByAssistant: true,
          },
        },
      }),
      "utf8",
    );

    const store = new BridgeStore(directory);
    await store.init();
    assert.equal(store.getSession(sessionId)?.managedByAssistant, false);

    const persisted = JSON.parse(
      await readFile(path.join(directory, "sessions.json"), "utf8"),
    ) as { sessions: Record<string, { managedByAssistant?: boolean }> };
    assert.equal(persisted.sessions[sessionId]?.managedByAssistant, false);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("init repairs legacy hidden sessions that are already active", async () => {
  const directory = await temporaryDirectory();
  const store = new BridgeStore(directory);
  try {
    const sessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    await writeFile(
      path.join(directory, "sessions.json"),
      JSON.stringify({
        sessions: {
          [sessionId]: {
            sessionId,
            shortId: "9b45397b",
            cwd: directory,
            projectName: "project",
            status: "waiting",
            openedAt: "2026-08-01T00:00:00.000Z",
            lastSeenAt: "2026-08-01T00:01:00.000Z",
            managedByAssistant: true,
            historyHiddenAt: "2026-07-31T00:00:00.000Z",
          },
        },
      }),
      "utf8",
    );
    await store.init();
    assert.equal(store.getSession(sessionId)?.historyHiddenAt, undefined);
    assert.equal(store.listAssistantManagedSessions().length, 1);
  } finally {
    await store.close();
    await rm(directory, { recursive: true, force: true });
  }
});

test("close flushes debounced writes and cancels background timers", async () => {
  const directory = await temporaryDirectory();
  const store = new BridgeStore(directory, { persistDebounceMs: 10_000 });
  try {
    await store.init();
    await store.upsertSession({
      sessionId: "close-flush-session",
      cwd: directory,
      status: "waiting",
      managedByAssistant: true,
    });
    await store.close();
    const persisted = JSON.parse(
      await readFile(path.join(directory, "sessions.json"), "utf8"),
    ) as { sessions: Record<string, unknown> };
    assert.ok(persisted.sessions["close-flush-session"]);
  } finally {
    await store.close();
    await rm(directory, { recursive: true, force: true });
  }
});

test("moves a managed placeholder's Feishu group to the real session", async () => {
  const directory = await temporaryDirectory();
  try {
    const store = new BridgeStore(directory);
    await store.init();
    await store.upsertSession({
      sessionId: "managed-terminal-placeholder",
      cwd: directory,
      status: "ready",
      source: "managed_window",
      managedTerminalId: "terminal-placeholder",
      managedByAssistant: true,
    });
    await store.setSessionFeishuChat("managed-terminal-placeholder", {
      chatId: "oc_session_group",
      chatName: "Codex｜project",
    });
    await store.upsertSession({
      sessionId: "019faef0-d0bb-7703-af82-17ee9b45397b",
      cwd: directory,
      status: "waiting",
      managedTerminalId: "terminal-placeholder",
      managedByAssistant: true,
    });

    await store.replaceSessionReferences(
      "managed-terminal-placeholder",
      "019faef0-d0bb-7703-af82-17ee9b45397b",
    );
    assert.equal(store.getSession("managed-terminal-placeholder"), undefined);
    assert.equal(
      store.getSession("019faef0-d0bb-7703-af82-17ee9b45397b")?.feishuChatId,
      "oc_session_group",
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("coalesces session writes within the debounce window", async () => {
  const directory = await temporaryDirectory();
  try {
    const store = new BridgeStore(directory, { persistDebounceMs: 1_000 });
    await store.init();
    for (let index = 0; index < 5; index += 1) {
      await store.upsertSession({
        sessionId: `coalesced-${index}`,
        cwd: directory,
        status: "waiting",
      });
    }
    const before = await readFile(path.join(directory, "sessions.json"), "utf8").catch(
      () => "",
    );
    assert.ok(!before.includes("coalesced-0"));

    await store.flushPending();
    const persisted = JSON.parse(
      await readFile(path.join(directory, "sessions.json"), "utf8"),
    ) as { sessions: Record<string, unknown> };
    assert.equal(Object.keys(persisted.sessions).length, 5);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("prunes ended sessions older than the retention window", async () => {
  const directory = await temporaryDirectory();
  try {
    const oldEndedAt = "2025-01-01T00:00:00.000Z";
    const oldId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    const groupedOldId = "019faef0-d0bb-7703-af82-17ee9b45397c";
    const freshId = "019fb4e7-d831-7dd3-9745-42f85a8209bb";
    const freshEndedAt = "2026-07-30T00:00:00.000Z";
    await writeFile(
      path.join(directory, "sessions.json"),
      JSON.stringify({
        sessions: {
          [oldId]: {
            sessionId: oldId,
            shortId: "9b45397b",
            cwd: directory,
            projectName: "old",
            status: "ended",
            openedAt: oldEndedAt,
            lastSeenAt: oldEndedAt,
            endedAt: oldEndedAt,
            managedByAssistant: true,
          },
          [freshId]: {
            sessionId: freshId,
            shortId: "5a8209bb",
            cwd: directory,
            projectName: "fresh",
            status: "ended",
            openedAt: freshEndedAt,
            lastSeenAt: freshEndedAt,
            endedAt: freshEndedAt,
            managedByAssistant: true,
          },
          [groupedOldId]: {
            sessionId: groupedOldId,
            shortId: "9b45397c",
            cwd: directory,
            projectName: "old-grouped",
            status: "ended",
            openedAt: oldEndedAt,
            lastSeenAt: oldEndedAt,
            endedAt: oldEndedAt,
            managedByAssistant: true,
            feishuChatId: "old-session-chat",
            feishuChatName: "old group",
            feishuChatCreatedAt: oldEndedAt,
          },
        },
      }),
      "utf8",
    );
    const store = new BridgeStore(directory, {
      endedSessionRetentionMs: 30 * 24 * 60 * 60 * 1000,
    });
    await store.init();
    assert.equal(store.getSession(oldId), undefined);
    assert.ok(store.getSession(freshId));
    assert.equal(store.getSession(groupedOldId), undefined);
    const persisted = JSON.parse(
      await readFile(path.join(directory, "sessions.json"), "utf8"),
    ) as { sessions: Record<string, unknown> };
    assert.equal(Object.keys(persisted.sessions).length, 1);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("quarantines malformed stores and replaces them with valid defaults", async () => {
  const directory = await temporaryDirectory();
  try {
    await writeFile(path.join(directory, "bindings.json"), "{not-json", "utf8");
    await writeFile(path.join(directory, "sessions.json"), "null", "utf8");

    const store = new BridgeStore(directory);
    await store.init();
    assert.ok(store.getPairingCode());
    assert.deepEqual(store.listOpenSessions(), []);

    const files = await readdir(directory);
    assert.ok(files.some((name) => name.startsWith("bindings.json.corrupt-")));
    assert.ok(files.some((name) => name.startsWith("sessions.json.corrupt-")));
    const bindings = JSON.parse(
      await readFile(path.join(directory, "bindings.json"), "utf8"),
    ) as { users?: unknown; pairingCode?: unknown };
    const sessions = JSON.parse(
      await readFile(path.join(directory, "sessions.json"), "utf8"),
    ) as { sessions?: unknown };
    assert.equal(typeof bindings.users, "object");
    assert.equal(typeof bindings.pairingCode, "string");
    assert.equal(typeof sessions.sessions, "object");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("prunes stale pending and resolved approvals from memory and disk", async () => {
  const directory = await temporaryDirectory();
  try {
    const store = new BridgeStore(directory, { persistDebounceMs: 10_000 });
    await store.init();
    const oldTimestamp = "2025-01-01T00:00:00.000Z";
    for (const [requestId, status] of [
      ["old-pending", "pending"],
      ["old-resolved", "resolved"],
    ] as const) {
      await store.createApproval({
        requestId,
        sessionId: "approval-session",
        turnId: `turn-${requestId}`,
        cwd: directory,
        toolName: "shell_command",
        toolPreview: "echo test",
        createdAt: oldTimestamp,
        expiresAt: oldTimestamp,
        status,
        ...(status === "resolved"
          ? { resolution: "allow" as const, resolvedAt: oldTimestamp }
          : {}),
        messageIds: [],
      });
    }
    await store.flushPending();
    assert.equal(store.getApproval("old-pending"), undefined);
    assert.equal(store.getApproval("old-resolved"), undefined);
    const persisted = JSON.parse(
      await readFile(path.join(directory, "approvals.json"), "utf8"),
    ) as { requests: Record<string, unknown> };
    assert.deepEqual(persisted.requests, {});
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("turn notification claims are durable and recoverable", async () => {
  const directory = await temporaryDirectory();
  try {
    const store = new BridgeStore(directory);
    await store.init();
    await store.upsertSession({
      sessionId: "notification-session",
      cwd: directory,
      status: "waiting",
    });

    assert.equal(
      await store.claimTurnNotification(
        "notification-session",
        "turn-1",
        "error",
        "temporary failure",
      ),
      true,
    );
    assert.equal(store.getSession("notification-session")?.lastNotificationStatus, "pending");
    assert.equal(store.getSession("notification-session")?.pendingNotificationKind, "error");
    assert.equal(
      store.getSession("notification-session")?.pendingNotificationMessage,
      "temporary failure",
    );
    assert.deepEqual(
      store.listPendingTurnNotifications().map((session) => session.sessionId),
      ["notification-session"],
    );

    await store.completeTurnNotification("notification-session", "turn-1");
    assert.equal(store.getSession("notification-session")?.lastNotificationStatus, "sent");
    assert.equal(
      await store.claimTurnNotification("notification-session", "turn-1"),
      false,
    );

    assert.equal(
      await store.claimTurnNotification("notification-session", "turn-2"),
      true,
    );
    await store.releaseTurnNotification("notification-session", "turn-2");
    assert.equal(store.getSession("notification-session")?.lastNotificationTurnId, undefined);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("retains dirty state and allows close to retry after a failed flush", async () => {
  const directory = await temporaryDirectory();
  const sessionFile = path.join(directory, "sessions.json");
  try {
    const store = new BridgeStore(directory, { persistDebounceMs: 10_000 });
    await store.init();
    await store.upsertSession({
      sessionId: "retry-close-session",
      cwd: directory,
      status: "waiting",
    });
    await mkdir(sessionFile);
    await assert.rejects(store.close());
    await rm(sessionFile, { recursive: true, force: true });
    await store.close();
    const persisted = JSON.parse(await readFile(sessionFile, "utf8")) as {
      sessions: Record<string, unknown>;
    };
    assert.ok(persisted.sessions["retry-close-session"]);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
