import assert from "node:assert/strict";
import test from "node:test";

import { OpenCodeManager } from "../src/opencode-manager.js";
import { FakeOpenCodeServer } from "./helpers/fake-opencode.js";

test("launch retries until the opencode server is healthy and seeds sessions", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
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
      basePort: port,
      maxPort: port,
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

test("register exposes only the most recent top-level historical candidate", async () => {
  const fake = new FakeOpenCodeServer();
  fake.activeSessionIds = [];
  fake.sessions = [
    {
      id: "session-old",
      directory: "C:/demo",
      title: "old",
      time: { created: 100, updated: 200 },
    },
    {
      id: "session-current",
      directory: "C:/demo",
      title: "current",
      time: { created: 300, updated: 400 },
    },
    {
      id: "session-child-directory",
      directory: "C:/demo/subproject",
      title: "child directory",
      time: { created: 500, updated: 600 },
    },
    {
      id: "session-subagent",
      directory: "C:/demo",
      parentID: "session-current",
      title: "subagent",
      time: { created: 700, updated: 800 },
    },
  ];
  const port = await fake.listen();
  const created: string[] = [];
  const manager = new OpenCodeManager({
    onInstanceConnected: () => {},
    onInstanceDisconnected: () => {},
    eventHandlers: {
      onSessionCreated: (session) => created.push(session.id),
    },
  });
  try {
    await manager.register(port, "C:/demo");
    await waitFor(
      () => manager.findActiveInstanceBySession("session-current") !== undefined,
    );
    assert.deepEqual(created, ["session-current"]);
    assert.equal(manager.findInstanceBySession("session-old"), undefined);
    assert.equal(
      manager.findActiveInstanceBySession("session-child-directory"),
      undefined,
    );
    assert.equal(manager.findActiveInstanceBySession("session-subagent"), undefined);
  } finally {
    await manager.unregister(port);
    await fake.close();
  }
});

test("a fresh assistant launch waits for a live session instead of attaching history", async () => {
  const fake = new FakeOpenCodeServer();
  fake.activeSessionIds = [];
  fake.sessions = [{
    id: "session-history",
    directory: "C:/demo",
    time: { created: 100, updated: 200 },
  }];
  const port = await fake.listen();
  const manager = new OpenCodeManager(
    {
      onInstanceConnected: () => {},
      onInstanceDisconnected: () => {},
      eventHandlers: {},
    },
    {
      basePort: port,
      maxPort: port,
      enumerateLocalPorts: async () => [],
      isLocalPortAvailable: async () => true,
    },
  );
  try {
    await manager.launch("C:/demo");
    await waitFor(() => manager.getInstance(port) !== undefined);
    await new Promise((resolve) => setTimeout(resolve, 100));
    assert.equal(manager.findInstanceBySession("session-history"), undefined);

    fake.sendSse("session.created", {
      sessionID: "session-live",
      info: {
        id: "session-live",
        directory: "C:/demo",
        time: { created: 300, updated: 300 },
      },
    });
    await waitFor(
      () => manager.findActiveInstanceBySession("session-live") !== undefined,
    );
  } finally {
    await manager.unregister(port);
    await fake.close();
  }
});

test("a live session switch removes the previous session from the active view", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
  const manager = new OpenCodeManager({
    onInstanceConnected: () => {},
    onInstanceDisconnected: () => {},
    eventHandlers: {},
  });
  try {
    await manager.register(port, "C:/demo");
    await waitFor(
      () => manager.findActiveInstanceBySession("session-alpha") !== undefined,
    );
    fake.sendSse("session.updated", {
      sessionID: "session-beta",
      info: { id: "session-beta", directory: "C:/demo" },
    });
    await waitFor(
      () => manager.findActiveInstanceBySession("session-beta") !== undefined,
    );
    assert.equal(manager.findActiveInstanceBySession("session-alpha"), undefined);
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
      fake.requests.some((request) => request.url.includes("/prompt_async")),
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

test("permission replies recover a missing session mapping on a single instance", async () => {
  const fake = new FakeOpenCodeServer();
  fake.sessions = [];
  fake.activeSessionIds = [];
  fake.v2PermissionReplyStatus = 204;
  const port = await fake.listen();
  const manager = new OpenCodeManager({
    onInstanceConnected: () => {},
    onInstanceDisconnected: () => {},
    eventHandlers: {},
  });
  try {
    await manager.register(port, "C:/demo");
    assert.equal(manager.findInstanceBySession("session-from-permission"), undefined);
    await manager.replyPermission("session-from-permission", "permission-recovered", "once");
    assert.equal(fake.permissionReplyResponses["permission-recovered"], "once");
    assert.equal(manager.findInstanceBySession("session-from-permission")?.port, port);
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
    const internal = manager as unknown as {
      sessionMetadata: Map<string, unknown>;
    };
    assert.equal(internal.sessionMetadata.has("session-alpha"), true);
    await manager.unregister(port);
    assert.equal(manager.getInstance(port), undefined);
    assert.equal(manager.findInstanceBySession("session-alpha"), undefined);
    assert.equal(internal.sessionMetadata.has("session-alpha"), false);
  } finally {
    await fake.close();
  }
});

test("session deletion forgets metadata owned by that instance", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
  const manager = new OpenCodeManager({
    onInstanceConnected: () => {},
    onInstanceDisconnected: () => {},
    eventHandlers: {},
  });
  const internal = manager as unknown as {
    sessionMetadata: Map<string, unknown>;
  };
  try {
    await manager.register(port, "C:/demo");
    await waitFor(() => internal.sessionMetadata.has("session-alpha"));
    fake.sendSse("session.deleted", { sessionID: "session-alpha" });
    await waitFor(() => !internal.sessionMetadata.has("session-alpha"));
    assert.equal(manager.findInstanceBySession("session-alpha"), undefined);
  } finally {
    await manager.unregister(port);
    await fake.close();
  }
});

test("launch with a resumed session id seeds the session even when it is outside the current directory", async () => {
  const fake = new FakeOpenCodeServer();
  fake.sessions = [
    {
      id: "session-latest",
      title: "latest",
      directory: "C:/demo",
      model: "deepseek-v4-flash-free",
      time: { created: 300, updated: 400 },
    },
    {
      id: "session-resumed",
      title: "resumed",
      directory: "C:/other",
      model: "deepseek-v4-flash-free",
      time: { created: 100, updated: 200 },
    },
  ];
  fake.activeSessionIds = ["session-latest"];
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
    assert.ok(manager.findActiveInstanceBySession("session-resumed"));
    assert.equal(manager.findActiveInstanceBySession("session-latest"), undefined);
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

test("register seeds pending OpenCode questions and permissions", async () => {
  const fake = new FakeOpenCodeServer();
  fake.v2PermissionListStatus = 200;
  fake.permissions = [{
    id: "per_seed",
    sessionID: "session-alpha",
    action: "shell",
    resources: ["git status"],
    save: [],
    metadata: {},
  }];
  fake.questions = [{
    id: "que_seed",
    sessionID: "session-alpha",
    questions: [{
      header: "方式",
      question: "选择方式",
      options: [{ label: "A", description: "选 A" }],
    }],
  }];
  const port = await fake.listen();
  const permissions: string[] = [];
  const questions: string[] = [];
  const manager = new OpenCodeManager({
    onInstanceConnected: () => {},
    onInstanceDisconnected: () => {},
    eventHandlers: {
      onSessionCreated: () => {},
      onPermissionAsked: (permission) => permissions.push(permission.id),
      onQuestionAsked: (question) => questions.push(question.id),
    },
  });
  try {
    await manager.register(port, "C:/demo");
    await waitFor(() => permissions.includes("per_seed") && questions.includes("que_seed"));
  } finally {
    await manager.unregister(port);
    await fake.close();
  }
});

test("pending interaction seeding is independent and remembers its session first", async () => {
  const fake = new FakeOpenCodeServer();
  fake.sessions = [];
  fake.permissionListStatus = 500;
  fake.questions = [{
    id: "que_seed_independent",
    sessionID: "session-only-in-question",
    questions: [{
      header: "方式",
      question: "选择方式",
      options: [{ label: "A", description: "选 A" }],
    }],
  }];
  const port = await fake.listen();
  let manager: OpenCodeManager;
  let foundInstanceDuringDispatch = false;
  const questions: string[] = [];
  manager = new OpenCodeManager({
    onInstanceConnected: () => {},
    onInstanceDisconnected: () => {},
    eventHandlers: {
      onQuestionAsked: (question) => {
        questions.push(question.id);
        foundInstanceDuringDispatch = Boolean(
          manager.findInstanceBySession(question.sessionID),
        );
      },
    },
  });
  try {
    await manager.register(port, "C:/demo");
    await waitFor(() => questions.includes("que_seed_independent"));
    assert.equal(foundInstanceDuringDispatch, true);
  } finally {
    await manager.unregister(port);
    await fake.close();
  }
});

test("re-registering a healthy port replaces its old event subscription", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
  const manager = new OpenCodeManager({
    onInstanceConnected: () => {},
    onInstanceDisconnected: () => {},
    eventHandlers: {},
  });
  try {
    await manager.register(port, "C:/demo");
    await waitFor(() => fake.activeSseClients === 1);
    await manager.register(port, "C:/demo");
    await waitFor(() => fake.sseConnectionCount >= 2 && fake.activeSseClients === 1);
  } finally {
    await manager.unregister(port);
    await fake.close();
  }
});

test("a dropped healthy subscription reconnects with backoff without reseeding", async () => {
  const fake = new FakeOpenCodeServer();
  fake.v2PermissionListStatus = 200;
  fake.permissions = [{
    id: "per_reconnect",
    sessionID: "session-alpha",
    action: "shell",
  }];
  fake.questions = [{
    id: "que_reconnect",
    sessionID: "session-alpha",
    questions: [{
      header: "方式",
      question: "选择方式",
      options: [{ label: "A", description: "选 A" }],
    }],
  }];
  const port = await fake.listen();
  const connected: number[] = [];
  const created: string[] = [];
  const permissions: string[] = [];
  const questions: string[] = [];
  const manager = new OpenCodeManager(
    {
      onInstanceConnected: (connectedPort) => connected.push(connectedPort),
      onInstanceDisconnected: () => {},
      eventHandlers: {
        onSessionCreated: (session) => created.push(session.id),
        onPermissionAsked: (permission) => permissions.push(permission.id),
        onQuestionAsked: (question) => questions.push(question.id),
      },
    },
    {
      subscriptionRetryBaseMs: 80,
      subscriptionRetryMaxMs: 200,
      subscriptionStableMs: 1_000,
    },
  );
  try {
    await manager.register(port, "C:/demo");
    await waitFor(
      () =>
        fake.activeSseClients === 1 &&
        created.length === 1 &&
        permissions.length === 1 &&
        questions.length === 1,
    );

    const disconnectedAt = Date.now();
    fake.closeSseClients();
    await new Promise((resolve) => setTimeout(resolve, 30));
    assert.equal(fake.sseConnectionCount, 1);
    await waitFor(
      () => fake.sseConnectionCount >= 2 && fake.activeSseClients === 1,
    );
    assert.ok(Date.now() - disconnectedAt >= 60);
    await new Promise((resolve) => setTimeout(resolve, 80));

    assert.deepEqual(connected, [port]);
    assert.deepEqual(created, ["session-alpha"]);
    assert.deepEqual(permissions, ["per_reconnect"]);
    assert.deepEqual(questions, ["que_reconnect"]);
  } finally {
    await manager.unregister(port);
    await fake.close();
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
