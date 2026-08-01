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

function messageEvent(messageId: string, chatId: string, text: string) {
  return {
    sender: { sender_id: { open_id: "owner" } },
    message: {
      message_id: messageId,
      chat_id: chatId,
      chat_type: "group",
      message_type: "text",
      content: JSON.stringify({ text }),
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
    uploadTtlMs: 60_000,
    outboundFileMaxBytes: 1024 * 1024,
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
      onPermissionUpdated: (permission) => {
        void controller.handleOpenCodePermissionUpdated(permission);
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

test("an opencode permission is sent to Feishu and allow forwards once", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-opencode-"));
  const ctx = await setup(directory);
  try {
    await waitFor(() => Boolean(ctx.store.getSession("session-alpha")?.feishuChatId));
    await ctx.controller.handleOpenCodePermissionUpdated({
      id: "permission-1",
      sessionID: "session-alpha",
      type: "shell_command",
      input: { command: "rm -rf /tmp/x" },
    });
    const approval = ctx.store
      .listApprovals()
      .find((item) => item.opencodePermissionId === "permission-1");
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
      () => ctx.fakeOpenCode.permissionReplyResponses["permission-1"] !== undefined,
    );
    assert.equal(ctx.fakeOpenCode.permissionReplyResponses["permission-1"], "once");
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
