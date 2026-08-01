import assert from "node:assert/strict";
import test from "node:test";

import { OpenCodeManager } from "../src/opencode-manager.js";
import { FakeOpenCodeServer } from "./helpers/fake-opencode.js";

const BASE_PORT = 8100;

test("launch retries until the opencode server is healthy and seeds sessions", async () => {
  const port = BASE_PORT;
  const fake = new FakeOpenCodeServer();
  await fake.listenOn(port);
  fake.healthOk = false;
  const created: Array<string> = [];
  const manager = new OpenCodeManager(
    {
      onInstanceConnected: () => {},
      onInstanceDisconnected: () => {},
      eventHandlers: {
        onSessionCreated: (session) => created.push(session.id),
      },
    },
    {
      basePort: BASE_PORT,
      maxPort: BASE_PORT + 10,
      enumerateLocalPorts: async () => [],
      isLocalPortAvailable: async () => true,
    },
  );
  try {
    const launched = await manager.launch("C:/demo");
    assert.equal(launched.port, port);

    fake.healthOk = true;
    await waitFor(() => manager.getInstance(port) !== undefined);
    await waitFor(() => created.includes("session-alpha"));
    assert.ok(manager.findInstanceBySession("session-alpha"));
  } finally {
    await manager.unregister(port);
    await fake.close();
  }
});

test("register connects an existing opencode server on the given port", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
  const manager = new OpenCodeManager({
    onInstanceConnected: () => {},
    onInstanceDisconnected: () => {},
    eventHandlers: {
      onSessionCreated: () => {},
    },
  });
  try {
    await manager.register(port, "C:/demo");
    const instance = manager.getInstance(port);
    assert.ok(instance);
    assert.equal(instance.cwd, "C:/demo");
    await waitFor(() => manager.findInstanceBySession("session-alpha") !== undefined);
  } finally {
    await manager.unregister(port);
    await fake.close();
  }
});

test("sendPrompt and replyPermission route to the correct instance", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
  const manager = new OpenCodeManager({
    onInstanceConnected: () => {},
    onInstanceDisconnected: () => {},
    eventHandlers: {
      onSessionCreated: () => {},
    },
  });
  try {
    await manager.register(port, "C:/demo");
    await waitFor(() => manager.findInstanceBySession("session-alpha") !== undefined);
    await manager.sendPrompt("session-alpha", "继续");
    await manager.replyPermission("session-alpha", "permission-9", "once");
    await manager.replyPermission("session-alpha", "permission-10", "reject");
    assert.equal(fake.permissionReplyResponses["permission-9"], "once");
    assert.equal(fake.permissionReplyResponses["permission-10"], "reject");
    assert.ok(
      fake.requests.some((request) => request.url.endsWith("/prompt_async")),
    );

    await assert.rejects(
      manager.sendPrompt("unknown-session", "x"),
      /找不到对应的 opencode 实例/,
    );
  } finally {
    await manager.unregister(port);
    await fake.close();
  }
});

test("unregister closes the subscription and forgets its sessions", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
  const manager = new OpenCodeManager({
    onInstanceConnected: () => {},
    onInstanceDisconnected: () => {},
    eventHandlers: {
      onSessionCreated: () => {},
    },
  });
  try {
    await manager.register(port, "C:/demo");
    await waitFor(() => manager.findInstanceBySession("session-alpha") !== undefined);
    await manager.unregister(port);
    assert.equal(manager.getInstance(port), undefined);
    assert.equal(manager.findInstanceBySession("session-alpha"), undefined);
  } finally {
    await fake.close();
  }
});

test("launch with a resumed session id seeds the session even when it is outside the current directory", async () => {
  const fake = new FakeOpenCodeServer();
  fake.sessions = [
    {
      id: "session-resumed",
      title: "resumed",
      directory: "C:/other",
      model: "deepseek-v4-flash-free",
    },
  ];
  const port = await fake.listen();
  const created: Array<string> = [];
  const manager = new OpenCodeManager(
    {
      onInstanceConnected: () => {},
      onInstanceDisconnected: () => {},
      eventHandlers: {
        onSessionCreated: (session) => created.push(session.id),
      },
    },
    {
      basePort: port,
      maxPort: port,
      enumerateLocalPorts: async () => [],
      isLocalPortAvailable: async () => true,
    },
  );
  try {
    await manager.launch("C:/demo", "session-resumed");
    await waitFor(() => manager.findInstanceBySession("session-resumed") !== undefined);
    assert.ok(created.includes("session-resumed"));
  } finally {
    await manager.unregister(port);
    await fake.close();
  }
});

test("port allocation skips system listeners and pending ports", async () => {
  const manager = new OpenCodeManager(
    {
      onInstanceConnected: () => {},
      onInstanceDisconnected: () => {},
      eventHandlers: { onSessionCreated: () => {} },
    },
    {
      basePort: 7200,
      maxPort: 7202,
      autoDiscover: false,
      enumerateLocalPorts: async () => [7200],
      isLocalPortAvailable: async () => true,
    },
  );
  const first = await manager.launch("C:/a");
  const second = await manager.launch("C:/b");
  try {
    assert.equal(first.port, 7201);
    assert.equal(second.port, 7202);
  } finally {
    await manager.unregister(first.port);
    await manager.unregister(second.port);
  }
});

test("port allocation probes a listener missed by enumeration", async () => {
  const fake = new FakeOpenCodeServer();
  const occupiedPort = await fake.listen();
  if (occupiedPort >= 65535) {
    await fake.close();
    return;
  }
  const manager = new OpenCodeManager(
    {
      onInstanceConnected: () => {},
      onInstanceDisconnected: () => {},
      eventHandlers: {},
    },
    {
      basePort: occupiedPort,
      maxPort: Math.min(65535, occupiedPort + 20),
      autoDiscover: false,
      enumerateLocalPorts: async () => [],
    },
  );
  let allocatedPort: number | undefined;
  try {
    allocatedPort = (await manager.launch("C:/probe")).port;
    assert.notEqual(allocatedPort, occupiedPort);
  } finally {
    if (allocatedPort !== undefined) {
      await manager.unregister(allocatedPort);
    }
    await fake.close();
  }
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
