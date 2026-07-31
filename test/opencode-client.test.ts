import assert from "node:assert/strict";
import test from "node:test";

import { OpenCodeClient } from "../src/opencode-client.js";
import { FakeOpenCodeServer } from "./helpers/fake-opencode.js";

test("health and session listing", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
  try {
    const client = new OpenCodeClient(`http://127.0.0.1:${port}`);
    assert.equal(await client.health(), true);
    fake.healthOk = false;
    assert.equal(await client.health(), false);
    const sessions = await client.listSessions();
    assert.equal(sessions.length, 1);
    assert.equal(sessions[0]?.id, "session-alpha");
  } finally {
    await fake.close();
  }
});

test("createSession, sendPrompt, replyPermission, abort, undo", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
  try {
    const client = new OpenCodeClient(`http://127.0.0.1:${port}`);
    const created = await client.createSession("hello");
    assert.equal(created.title, "hello");
    assert.match(created.id, /^session-created-/);

    await client.sendPrompt("session-alpha", "继续", { model: "x", noReply: true });
    await client.replyPermission("session-alpha", "permission-1", "once");
    await client.abort("session-alpha");
    await client.undo("session-alpha");

    assert.equal(fake.permissionReplyResponses["permission-1"], "once");
    const promptCall = fake.requests.find((request) =>
      request.url.endsWith("/prompt_async"));
    assert.ok(promptCall);
    const body = promptCall?.body as {
      parts?: Array<{ type?: string; text?: string }>;
      model?: string;
      noReply?: boolean;
    };
    assert.equal(body.parts?.[0]?.type, "text");
    assert.equal(body.parts?.[0]?.text, "继续");
    assert.equal(body.model, "x");
    assert.equal(body.noReply, true);
  } finally {
    await fake.close();
  }
});

test("listMessages and extractLastAssistantText", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
  try {
    const client = new OpenCodeClient(`http://127.0.0.1:${port}`);
    const messages = await client.listMessages("session-alpha");
    const result = client.extractLastAssistantText(messages);
    assert.equal(result.text, "完成 ✅\n 第二段");
    assert.equal(result.hasError, false);
  } finally {
    await fake.close();
  }
});

test("subscribe decodes real-format SSE events and routes them to handlers", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
  try {
    const client = new OpenCodeClient(`http://127.0.0.1:${port}`);
    const events: string[] = [];
    const idleSessions: string[] = [];
    const permissions: string[] = [];
    const created: string[] = [];
    const deleted: string[] = [];
    const statuses: string[] = [];
    const errors: string[] = [];
    const subscription = client.subscribe({
      onEvent: (event) => events.push(event.type),
      onSessionIdle: (sessionId) => idleSessions.push(sessionId),
      onPermissionUpdated: (permission) => permissions.push(permission.id),
      onSessionCreated: (session) => created.push(session.id),
      onSessionDeleted: (sessionId) => deleted.push(sessionId),
      onSessionStatus: (sessionId, status) => statuses.push(`${sessionId}:${status}`),
      onSessionError: (sessionId, error) => errors.push(`${sessionId}:${error}`),
    });
    await new Promise((resolve) => setTimeout(resolve, 100));
    fake.sendSse("session.created", {
      sessionID: "session-alpha",
      info: { id: "session-alpha", directory: "C:/demo" },
    });
    fake.sendSse("session.deleted", { info: { id: "session-alpha" } });
    fake.sendSse("session.idle", { sessionID: "session-alpha" });
    fake.sendSse("session.status", {
      sessionID: "session-alpha",
      status: { type: "busy" },
    });
    fake.sendSse("session.error", {
      sessionID: "session-alpha",
      error: { name: "UnknownError", data: { message: "boom" } },
    });
    fake.sendSse("permission.updated", {
      id: "permission-2",
      sessionID: "session-alpha",
      type: "shell_command",
      input: { command: "rm -rf x" },
    });
    fake.sendSse("message.part.updated", {
      sessionID: "session-alpha",
      messageID: "msg-1",
      part: {
        id: "part-1",
        type: "tool",
        tool: "shell_command",
        state: { status: "running", input: { command: "echo hi" } },
      },
    });
    await new Promise((resolve) => setTimeout(resolve, 100));
    assert.ok(events.includes("session.idle"));
    assert.ok(events.includes("permission.updated"));
    assert.ok(events.includes("message.part.updated"));
    assert.deepEqual(created, ["session-alpha"]);
    assert.deepEqual(deleted, ["session-alpha"]);
    assert.deepEqual(idleSessions, ["session-alpha"]);
    assert.deepEqual(statuses, ["session-alpha:busy"]);
    assert.deepEqual(errors, ["session-alpha:boom"]);
    assert.deepEqual(permissions, ["permission-2"]);
    subscription.close();
  } finally {
    await fake.close();
  }
});

test("user prompt is synthesized from message.updated plus its text part", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
  try {
    const client = new OpenCodeClient(`http://127.0.0.1:${port}`);
    const updated: Array<{ role?: string; text?: string }> = [];
    const subscription = client.subscribe({
      onMessageUpdated: (message) =>
        updated.push({ role: message.role, text: message.parts?.[0]?.text }),
    });
    await new Promise((resolve) => setTimeout(resolve, 100));
    fake.sendSse("message.updated", {
      sessionID: "session-alpha",
      info: {
        id: "msg-u1",
        role: "user",
        sessionID: "session-alpha",
        time: { created: 1 },
      },
    });
    fake.sendSse("message.part.updated", {
      sessionID: "session-alpha",
      part: {
        id: "prt-1",
        type: "text",
        text: "你好，请继续",
        messageID: "msg-u1",
        sessionID: "session-alpha",
      },
    });
    await new Promise((resolve) => setTimeout(resolve, 100));
    assert.deepEqual(updated, [{ role: "user", text: "你好，请继续" }]);
    subscription.close();
  } finally {
    await fake.close();
  }
});

test("legacy event: framing still parses", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
  try {
    const client = new OpenCodeClient(`http://127.0.0.1:${port}`);
    const idleSessions: string[] = [];
    const subscription = client.subscribe({
      onSessionIdle: (sessionId) => idleSessions.push(sessionId),
    });
    await new Promise((resolve) => setTimeout(resolve, 100));
    fake.sendSseRaw('event: session.idle\ndata: {"sessionID":"session-legacy"}\n\n');
    await new Promise((resolve) => setTimeout(resolve, 100));
    assert.deepEqual(idleSessions, ["session-legacy"]);
    subscription.close();
  } finally {
    await fake.close();
  }
});
