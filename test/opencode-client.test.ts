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
    assert.deepEqual(await client.listActiveSessionIds(), ["session-alpha"]);
  } finally {
    await fake.close();
  }
});

test("getSession returns a session by id and undefined when missing", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
  try {
    const client = new OpenCodeClient(`http://127.0.0.1:${port}`);
    const session = await client.getSession("session-alpha");
    assert.equal(session?.id, "session-alpha");
    assert.equal(session?.directory, "C:/demo");
    assert.equal(await client.getSession("session-ghost"), undefined);
  } finally {
    await fake.close();
  }
});

test("createSession, sendPrompt, replyPermission, abort, undo", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
  try {
    const client = new OpenCodeClient(`http://127.0.0.1:${port}`, "C:/demo");
    const created = await client.createSession("hello");
    assert.equal(created.title, "hello");
    assert.match(created.id, /^session-created-/);

    await client.sendPrompt("session-alpha", "继续", {
      model: "openai/x",
      noReply: true,
    });
    await client.replyPermission("session-alpha", "permission-1", "once");
    await client.abort("session-alpha");
    await client.undo("session-alpha");

    assert.equal(fake.permissionReplyResponses["permission-1"], "once");
    const promptCall = fake.requests.find((request) =>
      request.url.includes("/prompt_async"));
    assert.ok(promptCall);
    const body = promptCall?.body as {
      parts?: Array<{ type?: string; text?: string }>;
      model?: { providerID?: string; modelID?: string };
      noReply?: boolean;
    };
    assert.equal(body.parts?.[0]?.type, "text");
    assert.equal(body.parts?.[0]?.text, "继续");
    assert.deepEqual(body.model, { providerID: "openai", modelID: "x" });
    assert.equal(body.noReply, true);
    assert.match(promptCall?.url ?? "", /directory=C%3A%2Fdemo/);
  } finally {
    await fake.close();
  }
});

test("question and permission APIs use OpenCode 1.18 with legacy fallback", async () => {
  const fake = new FakeOpenCodeServer();
  fake.permissions = [{
    id: "per_pending",
    sessionID: "session-alpha",
    permission: "bash",
    patterns: ["git status"],
    metadata: {},
    always: [],
  }];
  fake.questions = [{
    id: "que_pending",
    sessionID: "session-alpha",
    questions: [{
      header: "范围",
      question: "选择范围",
      options: [
        { label: "代码", description: "仅代码" },
        { label: "文档", description: "仅文档" },
      ],
      multiple: true,
      custom: false,
    }],
  }];
  const port = await fake.listen();
  try {
    const client = new OpenCodeClient(`http://127.0.0.1:${port}`, "C:/demo");
    assert.equal((await client.listPermissions())[0]?.permission, "bash");
    const question = (await client.listQuestions())[0];
    assert.equal(question?.questions[0]?.multiple, true);
    assert.equal(question?.questions[0]?.custom, false);

    await client.replyQuestion("que_pending", [["代码", "文档"]]);
    assert.deepEqual(fake.questionReplyAnswers.que_pending, [["代码", "文档"]]);

    fake.questions = [{
      id: "que_reject",
      sessionID: "session-alpha",
      questions: [],
    }];
    await client.rejectQuestion("que_reject");
    assert.deepEqual(fake.questionRejections, ["que_reject"]);

    fake.modernPermissionEndpoint = false;
    await client.replyPermission("session-alpha", "per_legacy", "reject");
    assert.equal(fake.permissionReplyResponses.per_legacy, "reject");
  } finally {
    await fake.close();
  }
});

test("OpenCode V2 permission APIs normalize wrapped requests and use the V2 reply route", async () => {
  const fake = new FakeOpenCodeServer();
  fake.v2PermissionListStatus = 200;
  fake.v2PermissionReplyStatus = 204;
  fake.permissions = [{
    id: "per_v2_pending",
    sessionID: "session-alpha",
    action: "shell",
    resources: ["git status", "git diff"],
    save: ["git *"],
    metadata: { command: "git status" },
    source: { type: "tool", messageID: "msg-1", callID: "call-1" },
  }];
  const port = await fake.listen();
  try {
    const client = new OpenCodeClient(`http://127.0.0.1:${port}`, "C:/demo");
    const permission = (await client.listPermissions())[0];
    assert.equal(permission?.id, "per_v2_pending");
    assert.equal(permission?.sessionID, "session-alpha");
    assert.equal(permission?.action, "shell");
    assert.deepEqual(permission?.resources, ["git status", "git diff"]);
    assert.deepEqual(permission?.save, ["git *"]);
    assert.equal(permission?.tool?.callID, "call-1");

    await client.replyPermission("session-alpha", "per_v2_pending", "once");
    assert.equal(fake.permissionReplyResponses.per_v2_pending, "once");
    assert.ok(
      fake.requests.some(
        (request) =>
          request.method === "POST" &&
          request.url === "/api/session/session-alpha/permission/per_v2_pending/reply",
      ),
    );
    assert.equal(
      fake.requests.some((request) => request.url.includes("/permission/per_v2_pending/reply?")),
      false,
    );
  } finally {
    await fake.close();
  }
});

test("missing legacy pending-interaction endpoints are treated as empty", async () => {
  const fake = new FakeOpenCodeServer();
  fake.permissionListStatus = 404;
  fake.questionListStatus = 405;
  const port = await fake.listen();
  try {
    const client = new OpenCodeClient(`http://127.0.0.1:${port}`, "C:/demo");
    assert.deepEqual(await client.listPermissions(), []);
    assert.deepEqual(await client.listQuestions(), []);
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

test("read-only requests retry one transient local reset", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
  try {
    const client = new OpenCodeClient(`http://127.0.0.1:${port}`);
    fake.resetNextRequests = 1;
    const sessions = await client.listSessions();
    assert.equal(sessions[0]?.id, "session-alpha");
  } finally {
    await fake.close();
  }
});

test("subscribe decodes OpenCode 1.18 SSE events and routes them", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
  try {
    const client = new OpenCodeClient(`http://127.0.0.1:${port}`);
    const events: string[] = [];
    const idleSessions: string[] = [];
    const permissions: string[] = [];
    const permissionDetails: string[] = [];
    const permissionReplies: string[] = [];
    const questions: string[] = [];
    const questionReplies: string[] = [];
    const questionRejections: string[] = [];
    const created: string[] = [];
    const updatedSessions: string[] = [];
    const deleted: string[] = [];
    const statuses: string[] = [];
    const errors: string[] = [];
    const subscription = client.subscribe({
      onEvent: (event) => events.push(event.type),
      onSessionIdle: (sessionId) => idleSessions.push(sessionId),
      onPermissionAsked: (permission) => {
        permissions.push(permission.id);
        permissionDetails.push(
          `${permission.id}:${permission.action ?? permission.permission}:${permission.resources?.join(",") ?? ""}`,
        );
      },
      onPermissionReplied: (reply) =>
        permissionReplies.push(`${reply.requestID}:${reply.reply}`),
      onQuestionAsked: (request) => questions.push(request.id),
      onQuestionReplied: (reply) =>
        questionReplies.push(`${reply.requestID}:${reply.answers[0]?.join(",")}`),
      onQuestionRejected: (reply) => questionRejections.push(reply.requestID),
      onSessionCreated: (session) => created.push(session.id),
      onSessionUpdated: (session) => updatedSessions.push(session.id),
      onSessionDeleted: (sessionId) => deleted.push(sessionId),
      onSessionStatus: (sessionId, status) => statuses.push(`${sessionId}:${status}`),
      onSessionError: (sessionId, error) => errors.push(`${sessionId}:${error}`),
    });
    await new Promise((resolve) => setTimeout(resolve, 100));
    fake.sendSse("session.created", {
      sessionID: "session-alpha",
      info: { id: "session-alpha", directory: "C:/demo" },
    });
    fake.sendSse("session.updated", {
      sessionID: "session-alpha",
      info: { id: "session-alpha", directory: "C:/demo", title: "updated" },
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
    fake.sendSse("permission.asked", {
      id: "per_2",
      sessionID: "session-alpha",
      permission: "bash",
      patterns: ["rm -rf x"],
      metadata: {},
      always: [],
    });
    fake.sendSse("permission.replied", {
      sessionID: "session-alpha",
      requestID: "per_2",
      reply: "once",
    });
    fake.sendSse("permission.v2.asked", {
      id: "per_v2_event",
      sessionID: "session-alpha",
      action: "shell",
      resources: ["npm test"],
      save: [],
      metadata: {},
      source: { type: "tool", messageID: "msg-v2", callID: "call-v2" },
    });
    fake.sendSse("permission.v2.replied", {
      sessionID: "session-alpha",
      requestID: "per_v2_event",
      reply: "reject",
    });
    fake.sendSse("question.asked", {
      id: "que_2",
      sessionID: "session-alpha",
      questions: [{
        header: "方式",
        question: "选择方式",
        options: [{ label: "A", description: "选 A" }],
        multiple: false,
        custom: true,
      }],
    });
    fake.sendSse("question.replied", {
      sessionID: "session-alpha",
      requestID: "que_2",
      answers: [["A"]],
    });
    fake.sendSse("question.rejected", {
      sessionID: "session-alpha",
      requestID: "que_3",
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
    assert.ok(events.includes("permission.asked"));
    assert.ok(events.includes("question.asked"));
    assert.ok(events.includes("message.part.updated"));
    assert.deepEqual(created, ["session-alpha"]);
    assert.deepEqual(updatedSessions, ["session-alpha"]);
    assert.deepEqual(deleted, ["session-alpha"]);
    assert.deepEqual(idleSessions, ["session-alpha"]);
    assert.deepEqual(statuses, ["session-alpha:busy"]);
    assert.deepEqual(errors, ["session-alpha:boom"]);
    assert.deepEqual(permissions, ["per_2", "per_v2_event"]);
    assert.deepEqual(permissionDetails, ["per_2:bash:", "per_v2_event:shell:npm test"]);
    assert.deepEqual(permissionReplies, ["per_2:once", "per_v2_event:reject"]);
    assert.deepEqual(questions, ["que_2"]);
    assert.deepEqual(questionReplies, ["que_2:A"]);
    assert.deepEqual(questionRejections, ["que_3"]);
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

test("user message.updated with inline text is dispatched immediately", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
  try {
    const client = new OpenCodeClient(`http://127.0.0.1:${port}`);
    const updated: string[] = [];
    const subscription = client.subscribe({
      onMessageUpdated: (message) => {
        updated.push(message.parts?.[0]?.text ?? "");
      },
    });
    await waitFor(() => fake.activeSseClients === 1);
    fake.sendSse("message.updated", {
      info: {
        id: "msg-inline",
        role: "user",
        sessionID: "session-alpha",
      },
      parts: [{ type: "text", text: "内联提示" }],
    });
    await waitFor(() => updated.length === 1);
    assert.deepEqual(updated, ["内联提示"]);
    subscription.close();
  } finally {
    await fake.close();
  }
});

test("pending user messages expire when no text part arrives", async () => {
  const fake = new FakeOpenCodeServer();
  const port = await fake.listen();
  try {
    const client = new OpenCodeClient(
      `http://127.0.0.1:${port}`,
      undefined,
      { pendingUserMessageTtlMs: 30 },
    );
    const internal = client as unknown as {
      pendingUserMessages: Map<string, unknown>;
    };
    const subscription = client.subscribe({});
    await waitFor(() => fake.activeSseClients === 1);
    fake.sendSse("message.updated", {
      info: {
        id: "msg-no-text",
        role: "user",
        sessionID: "session-alpha",
      },
    });
    await waitFor(() => internal.pendingUserMessages.size === 1);
    await waitFor(() => internal.pendingUserMessages.size === 0, 1_000);
    subscription.close();
  } finally {
    await fake.close();
  }
});

test("legacy event framing and CRLF framing still parse", async () => {
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
    fake.sendSseRaw('event: session.idle\r\ndata: {"sessionID":"session-crlf"}\r\n\r\n');
    await new Promise((resolve) => setTimeout(resolve, 100));
    assert.deepEqual(idleSessions, ["session-legacy", "session-crlf"]);
    subscription.close();
  } finally {
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
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
}
