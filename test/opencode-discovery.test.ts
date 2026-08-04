import assert from "node:assert/strict";
import test from "node:test";

import { OpenCodeManager } from "../src/opencode-manager.js";
import { FakeOpenCodeServer } from "./helpers/fake-opencode.js";

test("auto-discovery connects to a running opencode server and seeds its sessions", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
  const created: Array<string> = [];
  const connected: Array<number> = [];
  const manager = new OpenCodeManager(
    {
      onInstanceConnected: (connectedPort) => connected.push(connectedPort),
      onInstanceDisconnected: () => {},
      eventHandlers: { onSessionCreated: (session) => created.push(session.id) },
    },
    {
      autoDiscover: true,
      scanIntervalMs: 20,
      enumerateLocalPorts: async () => [port],
    },
  );
  try {
    manager.startAutoDiscovery();
    await waitFor(() => manager.getInstance(port) !== undefined);
    await waitFor(() => created.includes("session-alpha"));
    assert.deepEqual(connected, [port]);
    assert.ok(manager.findInstanceBySession("session-alpha"));
  } finally {
    manager.stopAutoDiscovery();
    await manager.unregister(port);
    await fake.close();
  }
});

test("auto-discovery skips ports that are already connected", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
  let connectedCount = 0;
  const manager = new OpenCodeManager(
    {
      onInstanceConnected: () => {
        connectedCount += 1;
      },
      onInstanceDisconnected: () => {},
      eventHandlers: { onSessionCreated: () => {} },
    },
    {
      autoDiscover: true,
      scanIntervalMs: 20,
      enumerateLocalPorts: async () => [port],
    },
  );
  try {
    manager.startAutoDiscovery();
    await waitFor(() => manager.getInstance(port) !== undefined);
    await new Promise((resolve) => setTimeout(resolve, 120));
    assert.equal(connectedCount, 1);
    assert.equal(manager.listInstances().length, 1);
  } finally {
    manager.stopAutoDiscovery();
    await manager.unregister(port);
    await fake.close();
  }
});

test("auto-discovery only seeds sessions within the instance project directory", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
  fake.currentDirectory = "K:/AI/codex+";
  fake.sessions = [
    {
      id: "in-project",
      title: "in project",
      directory: "K:/AI/codex+",
      model: "opencode/deepseek",
    },
    {
      id: "foreign",
      title: "foreign project",
      directory: "C:/Windows/System32",
      model: "opencode/deepseek",
    },
  ];
  const created: Array<string> = [];
  const manager = new OpenCodeManager(
    {
      onInstanceConnected: () => {},
      onInstanceDisconnected: () => {},
      eventHandlers: { onSessionCreated: (session) => created.push(session.id) },
    },
    {
      autoDiscover: true,
      scanIntervalMs: 20,
      enumerateLocalPorts: async () => [port],
    },
  );
  try {
    manager.startAutoDiscovery();
    await waitFor(() => created.includes("in-project"));
    assert.ok(!created.includes("foreign"));
  } finally {
    manager.stopAutoDiscovery();
    await manager.unregister(port);
    await fake.close();
  }
});

test("auto-discovery removes an instance once its server goes away", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
  const disconnected: Array<number> = [];
  const manager = new OpenCodeManager(
    {
      onInstanceConnected: () => {},
      onInstanceDisconnected: (disconnectedPort) => disconnected.push(disconnectedPort),
      eventHandlers: { onSessionCreated: () => {} },
    },
    {
      autoDiscover: true,
      scanIntervalMs: 20,
      enumerateLocalPorts: async () => [port],
    },
  );
  try {
    manager.startAutoDiscovery();
    await waitFor(() => manager.getInstance(port) !== undefined);
    await fake.close();
    await waitFor(() => manager.getInstance(port) === undefined);
    assert.ok(disconnected.includes(port));
    assert.equal(manager.findInstanceBySession("session-alpha"), undefined);
  } finally {
    manager.stopAutoDiscovery();
    await fake.close().catch(() => {});
  }
});

test("auto-discovery ignores ports that are not opencode servers", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
  fake.healthOk = false;
  let connectedCount = 0;
  const manager = new OpenCodeManager(
    {
      onInstanceConnected: () => {
        connectedCount += 1;
      },
      onInstanceDisconnected: () => {},
      eventHandlers: { onSessionCreated: () => {} },
    },
    {
      autoDiscover: true,
      scanIntervalMs: 20,
      enumerateLocalPorts: async () => [port],
    },
  );
  try {
    manager.startAutoDiscovery();
    await new Promise((resolve) => setTimeout(resolve, 120));
    assert.equal(connectedCount, 0);
    assert.equal(manager.getInstance(port), undefined);
  } finally {
    manager.stopAutoDiscovery();
    await fake.close();
  }
});

test("auto-discovery can be disabled via the option", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
  let connectedCount = 0;
  const manager = new OpenCodeManager(
    {
      onInstanceConnected: () => {
        connectedCount += 1;
      },
      onInstanceDisconnected: () => {},
      eventHandlers: { onSessionCreated: () => {} },
    },
    {
      autoDiscover: false,
      scanIntervalMs: 20,
      enumerateLocalPorts: async () => [port],
    },
  );
  try {
    manager.startAutoDiscovery();
    await new Promise((resolve) => setTimeout(resolve, 120));
    assert.equal(connectedCount, 0);
    assert.equal(manager.getInstance(port), undefined);
  } finally {
    manager.stopAutoDiscovery();
    await fake.close();
  }
});

test("stopping auto-discovery during a scan does not schedule another pass", async () => {
  let scans = 0;
  let releaseScan: (() => void) | undefined;
  const scanGate = new Promise<void>((resolve) => {
    releaseScan = resolve;
  });
  const manager = new OpenCodeManager(
    {
      onInstanceConnected: () => {},
      onInstanceDisconnected: () => {},
      eventHandlers: {},
    },
    {
      autoDiscover: true,
      scanIntervalMs: 10,
      enumerateLocalPorts: async () => {
        scans += 1;
        await scanGate;
        return [];
      },
    },
  );

  manager.startAutoDiscovery();
  await waitFor(() => scans === 1);
  manager.stopAutoDiscovery();
  releaseScan?.();
  await new Promise((resolve) => setTimeout(resolve, 80));
  assert.equal(scans, 1);
});

async function waitFor(
  predicate: () => boolean,
  timeoutMs = 5_000,
): Promise<void> {
  const started = Date.now();
  while (!predicate()) {
    if (Date.now() - started > timeoutMs) {
      throw new Error("waitFor timed out");
    }
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
}
