import assert from "node:assert/strict";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
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
