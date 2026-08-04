import assert from "node:assert/strict";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";

import { BridgeController } from "../src/bridge-controller.js";
import type { CodexExitResult, CodexRunner } from "../src/codex-runner.js";
import type { SessionRecord } from "../src/domain.js";
import type { FeishuGateway } from "../src/feishu.js";
import { ManagedTerminalRouter } from "../src/managed-terminal.js";
import { OpenCodeManager } from "../src/opencode-manager.js";
import { BridgeStore } from "../src/store.js";
import { FakeOpenCodeServer } from "./helpers/fake-opencode.js";

class FakeFeishu {
  readonly replies: Array<{ messageId: string; text: string }> = [];
  readonly cards: Array<{ chatId: string; card: Record<string, unknown>; messageId: string }> = [];
  readonly patchedCards: Array<{ messageId: string; card: Record<string, unknown> }> = [];
  readonly createdGroups: Array<{ ownerOpenId: string; name: string }> = [];
  private counter = 0;

  async replyText(messageId: string, text: string): Promise<string> {
    this.replies.push({ messageId, text });
    return `reply-${++this.counter}`;
  }

  async sendText(_chatId: string, text: string): Promise<string> {
    this.replies.push({ messageId: "new", text });
    return `message-${++this.counter}`;
  }

  async createSessionGroup(
    ownerOpenId: string,
    name: string,
  ): Promise<{ chatId: string; name: string }> {
    const chatId = `session-chat-${++this.counter}`;
    this.createdGroups.push({ ownerOpenId, name });
    return { chatId, name };
  }

  async updateSessionGroupName(): Promise<void> {}

  async sendCard(chatId: string, card: Record<string, unknown>): Promise<string> {
    const messageId = `card-${++this.counter}`;
    this.cards.push({ chatId, card, messageId });
    return messageId;
  }

  async patchCard(messageId: string, card: Record<string, unknown>): Promise<void> {
    this.patchedCards.push({ messageId, card });
  }

  async sendLocalFile(): Promise<string> {
    return `file-${++this.counter}`;
  }

  async downloadMessageResource(
    _messageId: string,
    _fileKey: string,
    _type: "image" | "file",
    destinationPath: string,
  ): Promise<number> {
    const data = Buffer.from("fake attachment");
    await writeFile(destinationPath, data);
    return data.length;
  }
}

class FakeCodex {
  isRunning(): boolean {
    return false;
  }

  async resume(
    _session: SessionRecord,
    _prompt: string,
    _onExit: (result: CodexExitResult) => void | Promise<void>,
  ): Promise<void> {
    throw new Error("Codex resume should not be used for opencode sessions");
  }
}

function messageEvent(
  messageId: string,
  chatId: string,
  text: string,
  parentId?: string,
) {
  return {
    sender: { sender_id: { open_id: "owner" } },
    message: {
      message_id: messageId,
      chat_id: chatId,
      chat_type: "group",
      message_type: "text",
      content: JSON.stringify({ text }),
      ...(parentId ? { parent_id: parentId } : {}),
    },
  };
}

async function bindOwner(store: BridgeStore): Promise<void> {
  const code = store.getPairingCode();
  assert.ok(code);
  await store.bindOwner(
    { openId: "owner", chatId: "chat-owner", chatType: "p2p", boundAt: new Date().toISOString() },
    code,
  );
}

function controllerConfig(directory: string) {
  return {
    bindCommand: "绑定",
    approvalTimeoutMs: 60_000,
    inputTimeoutMs: 60_000,
    sessionActiveMs: 60_000,
    uploadsDirectory: path.join(directory, "uploads"),
    inboundFileMaxBytes: 1024 * 1024,
    inboundAttachmentMaxCount: 4,
    uploadMaxFiles: 100,
    uploadMaxBytes: 100 * 1024 * 1024,
    uploadTtlMs: 60_000,
    outboundFileMaxBytes: 1024 * 1024,
    retryBaseDelayMs: 10,
  };
}

async function setup(directory: string) {
  const store = new BridgeStore(directory);
  await store.init();
  await bindOwner(store);
  const feishu = new FakeFeishu();
  const codex = new FakeCodex() as unknown as CodexRunner;
  const terminals = new ManagedTerminalRouter();
  const fakeOpenCode = new FakeOpenCodeServer();
  const port = await fakeOpenCode.listen();
  let controller: BridgeController;
  const opencode = new OpenCodeManager({
    onInstanceConnected: () => {},
    onInstanceDisconnected: (disconnectedPort) => {
      void controller.handleOpenCodeInstanceDisconnected(disconnectedPort);
    },
    eventHandlers: {
      onSessionCreated: (session) => {
        void controller.handleOpenCodeSessionCreated(session);
      },
      onSessionUpdated: (session) => {
        void controller.handleOpenCodeSessionCreated(session);
      },
      onSessionIdle: (sessionId) => {
        void controller.handleOpenCodeSessionIdle(sessionId);
      },
      onSessionError: (sessionId, error) => {
        void controller.handleOpenCodeSessionError(sessionId, error);
      },
      onSessionDeleted: (sessionId) => {
        void controller.handleOpenCodeSessionDeleted(sessionId);
      },
      onSessionStatus: (sessionId, status) => {
        void controller.handleOpenCodeSessionStatus(sessionId, status);
      },
      onPermissionAsked: (permission) => {
        void controller.handleOpenCodePermissionUpdated(permission);
      },
      onPermissionUpdated: (permission) => {
        void controller.handleOpenCodePermissionUpdated(permission);
      },
      onPermissionReplied: (reply) => {
        void controller.handleOpenCodePermissionReplied(reply);
      },
      onQuestionAsked: (request) => {
        void controller.handleOpenCodeQuestionAsked(request);
      },
      onQuestionReplied: (reply) => {
        void controller.handleOpenCodeQuestionReplied(reply);
      },
      onQuestionRejected: (rejection) => {
        void controller.handleOpenCodeQuestionRejected(rejection);
      },
      onMessageUpdated: () => {},
      onMessagePartUpdated: () => {},
      onSessionCompacted: () => {},
      onDisconnected: () => {},
    },
  });
  controller = new BridgeController(store, feishu, codex, terminals, opencode, controllerConfig(directory));
  await opencode.register(port, "C:/demo");
  return {
    store,
    feishu,
    fakeOpenCode,
    opencode,
    controller,
    port,
  };
}

test("opencode sessions are registered, grouped, and routable from Feishu", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-opencode-"));
  const ctx = await setup(directory);
  try {
    await waitFor(() => ctx.store.getSession("session-alpha") !== undefined);
    const registered = ctx.store.getSession("session-alpha");
    assert.ok(registered);
    assert.equal(registered.runtime, "opencode");
    assert.equal(registered.managedByAssistant, true);

    await waitFor(() => Boolean(ctx.store.getSession("session-alpha")?.feishuChatId));
    const session = ctx.store.getSession("session-alpha");
    const chatId = session!.feishuChatId!;

    const before = ctx.fakeOpenCode.requests.length;
    await ctx.controller.handleFeishuMessage(messageEvent("m1", chatId, "请实现一个示例函数"));
    await waitFor(() => ctx.fakeOpenCode.requests.length > before);
    assert.ok(
      ctx.fakeOpenCode.requests.some(
        (request) =>
          request.url.includes("/prompt_async") &&
          (request.body as { parts?: Array<{ text?: string }> })?.parts?.[0]?.text?.includes("示例函数"),
      ),
    );
    assert.ok(ctx.feishu.replies.some((reply) => reply.text === "opencode 已接收。"));

    const running = ctx.store.getSession("session-alpha");
    assert.equal(running?.status, "running");
  } finally {
    await ctx.opencode.unregister(ctx.port);
    await ctx.fakeOpenCode.close();
    await ctx.store.flushPending();
    await rm(directory, { recursive: true, force: true });
  }
});

test("opencode idle marks the session waiting and notifies the group", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-opencode-"));
  const ctx = await setup(directory);
  try {
    await waitFor(() => Boolean(ctx.store.getSession("session-alpha")?.feishuChatId));
    await ctx.controller.handleOpenCodeSessionIdle("session-alpha");
    const session = ctx.store.getSession("session-alpha");
    assert.ok(session);
    assert.equal(session.status, "waiting");
    assert.match(session.lastAssistantMessage ?? "", /完成/);
    assert.ok(ctx.feishu.cards.some((entry) => entry.chatId === session.feishuChatId));
  } finally {
    await ctx.opencode.unregister(ctx.port);
    await ctx.fakeOpenCode.close();
    await ctx.store.flushPending();
    await rm(directory, { recursive: true, force: true });
  }
});

test("opencode retry attempts reset after a successful idle event", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-opencode-retry-"));
  const ctx = await setup(directory);
  try {
    await ctx.store.updateSettings({
      autoRetryErrors: true,
      retryMaxAttempts: 1,
      retryJitterSeconds: 0,
    });
    await waitFor(() => Boolean(ctx.store.getSession("session-alpha")?.feishuChatId));
    const temporaryError =
      "We're currently experiencing high demand, which may cause temporary errors.";
    const retryRequests = () =>
      ctx.fakeOpenCode.requests.filter(
        (request) =>
          request.method === "POST" && request.url.includes("/prompt_async"),
      );

    await ctx.controller.handleOpenCodeSessionError("session-alpha", temporaryError);
    await waitFor(() => retryRequests().length === 1);
    assert.match(JSON.stringify(retryRequests()[0]?.body), /重试上一项任务/);

    await ctx.controller.handleOpenCodeSessionIdle("session-alpha");
    assert.equal(ctx.store.getSession("session-alpha")?.status, "waiting");

    await ctx.controller.handleOpenCodeSessionError("session-alpha", temporaryError);
    await waitFor(() => retryRequests().length === 2);
    const firstAttemptCards = ctx.feishu.cards.filter((entry) =>
      JSON.stringify(entry.card).includes("第 1/1 次")
    );
    assert.equal(firstAttemptCards.length, 2);
  } finally {
    await ctx.opencode.unregister(ctx.port);
    await ctx.fakeOpenCode.close();
    await ctx.store.flushPending();
    await rm(directory, { recursive: true, force: true });
  }
});

test("opencode queued prompts use the shared runtime queue and fail visibly on close", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-opencode-"));
  const ctx = await setup(directory);
  try {
    await waitFor(() => Boolean(ctx.store.getSession("session-alpha")?.feishuChatId));
    const chatId = ctx.store.getSession("session-alpha")!.feishuChatId!;

    await ctx.controller.handleFeishuMessage(messageEvent("queue-first", chatId, "先执行第一项"));
    await ctx.controller.handleFeishuMessage(messageEvent("queue-second", chatId, "再执行第二项"));

    assert.equal(ctx.controller.health().queuedPrompts, 1);
    await ctx.controller.handleOpenCodeSessionDeleted("session-alpha");
    assert.equal(ctx.controller.health().queuedPrompts, 0);
    assert.ok(
      ctx.feishu.replies.some(
        (reply) =>
          reply.messageId === "queue-second" &&
          reply.text.includes("opencode 未接收：会话已关闭"),
      ),
    );
  } finally {
    await ctx.opencode.unregister(ctx.port);
    await ctx.fakeOpenCode.close();
    await ctx.store.flushPending();
    await rm(directory, { recursive: true, force: true });
  }
});

test("an opencode permission is sent to Feishu and allow forwards once", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-opencode-"));
  const ctx = await setup(directory);
  try {
    await waitFor(() => Boolean(ctx.store.getSession("session-alpha")?.feishuChatId));
    await ctx.controller.handleOpenCodePermissionUpdated({
      id: "per_1",
      sessionID: "session-alpha",
      permission: "bash",
      patterns: ["rm -rf /tmp/x"],
      metadata: { command: "rm -rf /tmp/x" },
      always: [],
    });
    const approval = ctx.store
      .listApprovals()
      .find((item) => item.opencodePermissionId === "per_1");
    assert.ok(approval);
    assert.equal(approval.status, "pending");

    await ctx.controller.handleCardAction({
      operator: { open_id: "owner" },
      action: {
        value: JSON.stringify({
          action: "approval_allow",
          requestId: approval.requestId,
          sessionId: "session-alpha",
        }),
      },
    });
    await waitFor(
      () => ctx.fakeOpenCode.permissionReplyResponses.per_1 !== undefined,
    );
    assert.equal(ctx.fakeOpenCode.permissionReplyResponses.per_1, "once");
    const resolved = ctx.store.getApproval(approval.requestId);
    assert.equal(resolved?.status, "resolved");
    assert.equal(resolved?.resolution, "allow");
  } finally {
    await ctx.opencode.unregister(ctx.port);
    await ctx.fakeOpenCode.close();
    await ctx.store.flushPending();
    await rm(directory, { recursive: true, force: true });
  }
});

test("automatic OpenCode V2 approval is silent and uses the shared bridge settings", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-opencode-auto-"));
  const ctx = await setup(directory);
  try {
    await waitFor(() => Boolean(ctx.store.getSession("session-alpha")?.feishuChatId));
    await waitFor(() => ctx.fakeOpenCode.activeSseClients === 1);
    await ctx.store.updateSettings({ autoApprove: true });
    ctx.fakeOpenCode.v2PermissionReplyStatus = 204;
    const cardsBefore = ctx.feishu.cards.length;

    ctx.fakeOpenCode.sendSse("permission.v2.asked", {
      id: "per_v2_auto",
      sessionID: "session-alpha",
      action: "shell",
      resources: ["npm test"],
      save: [],
      metadata: { command: "npm test" },
      source: { type: "tool", messageID: "msg-v2-auto", callID: "call-v2-auto" },
    });

    await waitFor(() => ctx.fakeOpenCode.permissionReplyResponses.per_v2_auto === "once");
    const approval = ctx.store
      .listApprovals()
      .find((item) => item.opencodePermissionId === "per_v2_auto");
    assert.equal(approval?.status, "resolved");
    assert.equal(approval?.resolution, "allow");
    assert.equal(approval?.toolName, "shell");
    assert.match(approval?.toolPreview ?? "", /npm test/);
    assert.equal(ctx.feishu.cards.length, cardsBefore);
    assert.equal(ctx.controller.health().pendingApprovals, 0);
  } finally {
    await ctx.opencode.unregister(ctx.port);
    await ctx.fakeOpenCode.close();
    await ctx.store.flushPending();
    await rm(directory, { recursive: true, force: true });
  }
});

test("high-risk OpenCode permission remains pending with automatic approval enabled", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-opencode-risk-"));
  const ctx = await setup(directory);
  try {
    await waitFor(() => Boolean(ctx.store.getSession("session-alpha")?.feishuChatId));
    await ctx.store.updateSettings({ autoApprove: true });
    ctx.fakeOpenCode.v2PermissionReplyStatus = 204;
    const cardsBefore = ctx.feishu.cards.length;

    await ctx.controller.handleOpenCodePermissionUpdated({
      id: "per_high_risk",
      sessionID: "session-alpha",
      action: "shell",
      resources: ["rm -rf build"],
      metadata: { command: "rm -rf build" },
    });

    const approval = ctx.store
      .listApprovals()
      .find((item) => item.opencodePermissionId === "per_high_risk");
    assert.equal(approval?.status, "pending");
    assert.equal(approval?.riskLevel, "high");
    assert.equal(approval?.requiresManualApproval, true);
    assert.equal(ctx.fakeOpenCode.permissionReplyResponses.per_high_risk, undefined);
    assert.equal(ctx.feishu.cards.length, cardsBefore + 1);
    assert.match(JSON.stringify(ctx.feishu.cards.at(-1)?.card), /高风险操作需要确认/);
  } finally {
    await ctx.opencode.unregister(ctx.port);
    await ctx.fakeOpenCode.close();
    await ctx.store.flushPending();
    await rm(directory, { recursive: true, force: true });
  }
});

test("failed automatic OpenCode approval falls back to a manual card and can retry", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-opencode-fallback-"));
  const ctx = await setup(directory);
  try {
    await waitFor(() => Boolean(ctx.store.getSession("session-alpha")?.feishuChatId));
    await ctx.store.updateSettings({ autoApprove: true });
    ctx.fakeOpenCode.v2PermissionReplyStatus = 500;
    const cardsBefore = ctx.feishu.cards.length;
    const permission = {
      id: "per_auto_retry",
      sessionID: "session-alpha",
      action: "shell",
      resources: ["npm test"],
      save: [],
      metadata: { command: "npm test" },
    };

    await ctx.controller.handleOpenCodePermissionUpdated(permission);
    const pending = ctx.store
      .listApprovals()
      .find((item) => item.opencodePermissionId === permission.id);
    assert.equal(pending?.status, "pending");
    assert.equal(pending?.requiresManualApproval, true);
    assert.equal(ctx.controller.health().pendingApprovals, 1);
    assert.equal(ctx.feishu.cards.length, cardsBefore + 1);
    assert.match(JSON.stringify(ctx.feishu.cards.at(-1)?.card), /批准一次/);

    ctx.fakeOpenCode.v2PermissionReplyStatus = 204;
    await ctx.controller.handleOpenCodePermissionUpdated(permission);
    assert.equal(ctx.fakeOpenCode.permissionReplyResponses.per_auto_retry, "once");
    assert.equal(ctx.store.getApproval(pending!.requestId)?.resolution, "allow");
    await waitFor(
      () => ctx.feishu.patchedCards.some(
        (item) => item.messageId === ctx.feishu.cards.at(-1)?.messageId,
      ),
    );
    assert.equal(ctx.feishu.cards.length, cardsBefore + 1);
    assert.equal(ctx.controller.health().pendingApprovals, 0);
  } finally {
    await ctx.opencode.unregister(ctx.port);
    await ctx.fakeOpenCode.close();
    await ctx.store.flushPending();
    await rm(directory, { recursive: true, force: true });
  }
});

test("duplicate OpenCode interaction events create only one Feishu card", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-opencode-"));
  const ctx = await setup(directory);
  try {
    await waitFor(() => Boolean(ctx.store.getSession("session-alpha")?.feishuChatId));
    const cardsBefore = ctx.feishu.cards.length;
    const permission = {
      id: "per_duplicate",
      sessionID: "session-alpha",
      permission: "bash",
      patterns: ["git status"],
      metadata: {},
      always: [],
    };
    const question = {
      id: "que_duplicate",
      sessionID: "session-alpha",
      questions: [{
        header: "方式",
        question: "选择方式",
        options: [{ label: "A", description: "选 A" }],
        multiple: false,
        custom: true,
      }],
    };
    await Promise.all([
      ctx.controller.handleOpenCodePermissionUpdated(permission),
      ctx.controller.handleOpenCodePermissionUpdated(permission),
      ctx.controller.handleOpenCodeQuestionAsked(question),
      ctx.controller.handleOpenCodeQuestionAsked(question),
    ]);
    assert.equal(
      ctx.store.listApprovals().filter((item) => item.opencodePermissionId === permission.id).length,
      1,
    );
    assert.equal(ctx.feishu.cards.length - cardsBefore, 2);
    const approval = ctx.store
      .listApprovals()
      .find((item) => item.opencodePermissionId === permission.id);
    assert.match(approval?.toolPreview ?? "", /git status/);
    assert.equal(ctx.controller.health().pendingInputs, 1);
  } finally {
    await ctx.opencode.unregister(ctx.port);
    await ctx.fakeOpenCode.close();
    await ctx.store.flushPending();
    await rm(directory, { recursive: true, force: true });
  }
});

test("opencode single-choice buttons reply through the question API", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-opencode-"));
  const ctx = await setup(directory);
  try {
    await waitFor(() => Boolean(ctx.store.getSession("session-alpha")?.feishuChatId));
    const before = ctx.feishu.cards.length;
    await ctx.controller.handleOpenCodeQuestionAsked({
      id: "que_single",
      sessionID: "session-alpha",
      questions: [{
        header: "方式",
        question: "选择处理方式",
        options: [
          { label: "仅检查", description: "不改文件" },
          { label: "检查并修复", description: "直接修复" },
        ],
        multiple: false,
        custom: true,
      }],
    });
    const card = ctx.feishu.cards.slice(before).at(-1);
    assert.ok(card);
    assert.match(JSON.stringify(card.card), /检查并修复/);
    const actionValue = findCardAction(card.card, "input_answer", "检查并修复");
    assert.ok(actionValue);

    const result = await ctx.controller.handleCardAction({
      operator: { open_id: "owner" },
      action: { value: JSON.stringify(actionValue) },
    });
    assert.equal(result.toast?.type, "success");
    assert.deepEqual(ctx.fakeOpenCode.questionReplyAnswers.que_single, [["检查并修复"]]);
    await waitFor(() => ctx.feishu.patchedCards.some((item) => item.messageId === card.messageId));
    assert.equal(ctx.controller.health().pendingInputs, 0);
  } finally {
    await ctx.opencode.unregister(ctx.port);
    await ctx.fakeOpenCode.close();
    await ctx.store.flushPending();
    await rm(directory, { recursive: true, force: true });
  }
});

test("opencode multi-choice quoted replies preserve string[][] answers", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-opencode-"));
  const ctx = await setup(directory);
  try {
    await waitFor(() => Boolean(ctx.store.getSession("session-alpha")?.feishuChatId));
    const session = ctx.store.getSession("session-alpha")!;
    const before = ctx.feishu.cards.length;
    await ctx.controller.handleOpenCodeQuestionAsked({
      id: "que_multi",
      sessionID: "session-alpha",
      questions: [{
        header: "范围",
        question: "选择要处理的范围",
        options: [
          { label: "代码", description: "源代码" },
          { label: "测试", description: "自动化测试" },
          { label: "文档", description: "项目说明" },
        ],
        multiple: true,
        custom: false,
      }],
    });
    const card = ctx.feishu.cards.slice(before).at(-1);
    assert.ok(card);
    assert.match(JSON.stringify(card.card), /可多选/);

    await ctx.controller.handleFeishuMessage(
      messageEvent("multi-invalid", session.feishuChatId!, "1,自定义", card.messageId),
    );
    assert.equal(ctx.fakeOpenCode.questionReplyAnswers.que_multi, undefined);
    assert.equal(ctx.controller.health().pendingInputs, 1);

    await ctx.controller.handleFeishuMessage(
      messageEvent("multi-valid", session.feishuChatId!, "1,3", card.messageId),
    );
    assert.deepEqual(ctx.fakeOpenCode.questionReplyAnswers.que_multi, [["代码", "文档"]]);
    assert.equal(ctx.controller.health().pendingInputs, 0);
  } finally {
    await ctx.opencode.unregister(ctx.port);
    await ctx.fakeOpenCode.close();
    await ctx.store.flushPending();
    await rm(directory, { recursive: true, force: true });
  }
});

test("an opencode question answered locally closes the Feishu card", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-opencode-"));
  const ctx = await setup(directory);
  try {
    await waitFor(() => Boolean(ctx.store.getSession("session-alpha")?.feishuChatId));
    const before = ctx.feishu.cards.length;
    await ctx.controller.handleOpenCodeQuestionAsked({
      id: "que_local",
      sessionID: "session-alpha",
      questions: [{
        header: "确认",
        question: "继续吗",
        options: [{ label: "继续", description: "继续执行" }],
        multiple: false,
        custom: true,
      }],
    });
    const card = ctx.feishu.cards.slice(before).at(-1);
    assert.ok(card);
    await ctx.controller.handleOpenCodeQuestionReplied({
      sessionID: "session-alpha",
      requestID: "que_local",
      answers: [["本机回答"]],
    });
    await waitFor(() => ctx.feishu.patchedCards.some((item) => item.messageId === card.messageId));
    assert.match(
      JSON.stringify(ctx.feishu.patchedCards.find((item) => item.messageId === card.messageId)?.card),
      /本机回答/,
    );
    assert.equal(ctx.controller.health().pendingInputs, 0);
  } finally {
    await ctx.opencode.unregister(ctx.port);
    await ctx.fakeOpenCode.close();
    await ctx.store.flushPending();
    await rm(directory, { recursive: true, force: true });
  }
});

test("turning an OpenCode question back to the computer keeps it pending", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-opencode-"));
  const ctx = await setup(directory);
  try {
    await waitFor(() => Boolean(ctx.store.getSession("session-alpha")?.feishuChatId));
    const before = ctx.feishu.cards.length;
    await ctx.controller.handleOpenCodeQuestionAsked({
      id: "que_local_pending",
      sessionID: "session-alpha",
      questions: [{
        header: "确认",
        question: "继续吗",
        options: [{ label: "继续", description: "继续执行" }],
        multiple: false,
        custom: true,
      }],
    });
    const card = ctx.feishu.cards.slice(before).at(-1);
    assert.ok(card);
    const actionValue = findCardAction(card.card, "input_local");
    assert.ok(actionValue);
    const result = await ctx.controller.handleCardAction({
      operator: { open_id: "owner" },
      action: { value: JSON.stringify(actionValue) },
    });
    assert.equal(result.toast?.type, "success");
    assert.equal(ctx.controller.health().pendingInputs, 0);
    assert.equal(ctx.store.getSession("session-alpha")?.status, "pending_input");
  } finally {
    await ctx.opencode.unregister(ctx.port);
    await ctx.fakeOpenCode.close();
    await ctx.store.flushPending();
    await rm(directory, { recursive: true, force: true });
  }
});

test("OpenCode replies update session state even when no Feishu waiter exists", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-opencode-"));
  const ctx = await setup(directory);
  try {
    await waitFor(() => ctx.store.getSession("session-alpha") !== undefined);
    await ctx.controller.handleOpenCodeQuestionReplied({
      sessionID: "session-alpha",
      requestID: "que_answered_elsewhere",
      answers: [["A"]],
    });
    assert.equal(ctx.store.getSession("session-alpha")?.status, "running");
    await ctx.controller.handleOpenCodeQuestionRejected({
      sessionID: "session-alpha",
      requestID: "que_rejected_elsewhere",
    });
    assert.equal(ctx.store.getSession("session-alpha")?.status, "waiting");
  } finally {
    await ctx.opencode.unregister(ctx.port);
    await ctx.fakeOpenCode.close();
    await ctx.store.flushPending();
    await rm(directory, { recursive: true, force: true });
  }
});

test("a disconnected opencode instance ends its sessions", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-opencode-"));
  const ctx = await setup(directory);
  try {
    await waitFor(() => ctx.store.getSession("session-alpha") !== undefined);
    await ctx.opencode.unregister(ctx.port);
    await waitFor(
      () => ctx.store.getSession("session-alpha")?.status === "ended",
    );
  } finally {
    await ctx.fakeOpenCode.close();
    await ctx.store.flushPending();
    await rm(directory, { recursive: true, force: true });
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

function findCardAction(
  value: unknown,
  action: string,
  answer?: string,
): Record<string, unknown> | undefined {
  if (!value || typeof value !== "object") {
    return undefined;
  }
  if (Array.isArray(value)) {
    for (const item of value) {
      const found = findCardAction(item, action, answer);
      if (found) return found;
    }
    return undefined;
  }
  const record = value as Record<string, unknown>;
  if (
    record.action === action &&
    (answer === undefined || record.answer === answer)
  ) {
    return record;
  }
  for (const child of Object.values(record)) {
    const found = findCardAction(child, action, answer);
    if (found) return found;
  }
  return undefined;
}
