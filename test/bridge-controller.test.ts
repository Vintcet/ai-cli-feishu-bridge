import assert from "node:assert/strict";
import {
  appendFile,
  mkdtemp,
  readFile,
  realpath,
  rm,
  stat,
  writeFile,
} from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";

import { BridgeController } from "../src/bridge-controller.js";
import type { CodexExitResult, CodexRunner } from "../src/codex-runner.js";
import type { ApprovalRecord, SessionRecord } from "../src/domain.js";
import type { FeishuGateway } from "../src/feishu.js";
import { ManagedTerminalRouter } from "../src/managed-terminal.js";
import type { OpenCodeManager } from "../src/opencode-manager.js";
import { BridgeStore } from "../src/store.js";

class FakeFeishu {
  readonly replies: Array<{ messageId: string; text: string }> = [];
  readonly cards: Array<{ chatId: string; card: Record<string, unknown>; messageId: string }> = [];
  readonly patchedCards: Array<{ messageId: string; card: Record<string, unknown> }> = [];
  readonly localFiles: Array<{ chatId: string; filePath: string }> = [];
  readonly createdGroups: Array<{
    ownerOpenId: string;
    name: string;
    description: string;
    chatId: string;
  }> = [];
  readonly renamedGroups: Array<{ chatId: string; name: string }> = [];
  readonly deletedGroups: string[] = [];
  readonly cardIdempotencyKeys: string[] = [];
  readonly cardIdempotencyAttempts: string[] = [];
  readonly failCardSendAttempts = new Set<number>();
  private readonly idempotentCardMessages = new Map<string, string>();
  private counter = 0;
  private cardSendAttempts = 0;
  createGroupError?: Error;
  deleteGroupError?: Error;

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
    description: string,
  ): Promise<{ chatId: string; name: string }> {
    if (this.createGroupError) {
      throw this.createGroupError;
    }
    const chatId = `session-chat-${++this.counter}`;
    this.createdGroups.push({ ownerOpenId, name, description, chatId });
    return { chatId, name };
  }

  async updateSessionGroupName(chatId: string, name: string): Promise<void> {
    this.renamedGroups.push({ chatId, name });
  }

  async deleteSessionGroup(chatId: string): Promise<void> {
    if (this.deleteGroupError) {
      throw this.deleteGroupError;
    }
    this.deletedGroups.push(chatId);
  }

  async sendCard(
    chatId: string,
    card: Record<string, unknown>,
    idempotencyKey?: string,
  ): Promise<string> {
    if (idempotencyKey) {
      this.cardIdempotencyAttempts.push(idempotencyKey);
    }
    const existingMessageId = idempotencyKey
      ? this.idempotentCardMessages.get(idempotencyKey)
      : undefined;
    if (existingMessageId) {
      return existingMessageId;
    }
    this.cardSendAttempts += 1;
    if (this.failCardSendAttempts.has(this.cardSendAttempts)) {
      throw new Error(`simulated card send failure ${this.cardSendAttempts}`);
    }
    const messageId = `card-${++this.counter}`;
    this.cards.push({ chatId, card, messageId });
    if (idempotencyKey) {
      this.cardIdempotencyKeys.push(idempotencyKey);
      this.idempotentCardMessages.set(idempotencyKey, messageId);
    }
    return messageId;
  }

  async patchCard(messageId: string, card: Record<string, unknown>): Promise<void> {
    this.patchedCards.push({ messageId, card });
  }

  async sendLocalFile(chatId: string, filePath: string): Promise<string> {
    this.localFiles.push({ chatId, filePath });
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
  resumeCount = 0;
  prompts: string[] = [];
  private running = false;
  private onExit?: (result: CodexExitResult) => void | Promise<void>;

  isRunning(): boolean {
    return this.running;
  }

  async resume(
    _session: SessionRecord,
    prompt: string,
    _onExit: (result: CodexExitResult) => void | Promise<void>,
  ): Promise<void> {
    this.resumeCount += 1;
    this.prompts.push(prompt);
    this.running = true;
    this.onExit = _onExit;
  }

  async finish(result: CodexExitResult = { code: 0, signal: null, stderr: "" }): Promise<void> {
    const onExit = this.onExit;
    this.running = false;
    this.onExit = undefined;
    await onExit?.(result);
  }
}

class FakeOpenCodeManager {
  connected = false;
  readonly routableSessions = new Set(["opencode-session-alpha"]);
  readonly activeSessions = new Set(["opencode-session-alpha"]);
  readonly prompts: Array<{ sessionId: string; prompt: string }> = [];

  findInstanceBySession(sessionId: string) {
    return this.connected && this.routableSessions.has(sessionId)
      ? { port: 5100, cwd: "C:/demo" }
      : undefined;
  }

  findActiveInstanceBySession(sessionId: string) {
    return this.connected && this.activeSessions.has(sessionId)
      ? { port: 5100, cwd: "C:/demo" }
      : undefined;
  }

  listInstances() {
    return this.connected ? [{ port: 5100, cwd: "C:/demo" }] : [];
  }

  hasPendingSession(): boolean {
    return false;
  }

  async sendPrompt(sessionId: string, prompt: string): Promise<void> {
    this.prompts.push({ sessionId, prompt });
  }
}

class FakeManagedTerminals {
  readonly sends: Array<{ prompt: string; submitMode: string }> = [];
  private online: boolean;

  constructor(
    private readonly terminalId: string,
    private readonly cwd: string,
    private readonly sessionId: string,
    online = true,
  ) {
    this.online = online;
  }

  isManaged(session: SessionRecord): boolean {
    return session.managedTerminalId === this.terminalId;
  }

  isOnline(): boolean {
    return this.online;
  }

  isReady(): boolean {
    return this.online;
  }

  claimById() {
    this.online = true;
    return {
      terminalId: this.terminalId,
      elevated: false,
      createdAt: Date.now() - 1_000,
    };
  }

  listOnline() {
    if (!this.online) {
      return [];
    }
    const now = Date.now();
    return [{
      terminalId: this.terminalId,
      cwd: this.cwd,
      normalizedCwd: this.cwd,
      elevated: false,
      ready: true,
      createdAt: now - 1_000,
      lastSeenAt: now,
      sessionId: this.sessionId,
    }];
  }

  async send(_session: SessionRecord, prompt: string, submitMode: string): Promise<void> {
    this.sends.push({ prompt, submitMode });
  }
}

function controllerConfig(directory: string) {
  return {
    bindCommand: "绑定",
    approvalTimeoutMs: 10_000,
    inputTimeoutMs: 10_000,
    sessionActiveMs: 60_000,
    uploadsDirectory: path.join(directory, "uploads"),
    inboundFileMaxBytes: 1024 * 1024,
    inboundAttachmentMaxCount: 4,
    uploadMaxFiles: 100,
    uploadMaxBytes: 100 * 1024 * 1024,
    uploadTtlMs: 60_000,
    outboundFileMaxBytes: 1024 * 1024,
    retryBaseDelayMs: 10,
    transcriptPollIntervalMs: 10,
    liveClientProcessIds: (clients: Array<{ processId: number }>) =>
      new Set(
        clients
          .filter((client) => client.processId === process.pid)
          .map((client) => client.processId),
      ),
  };
}

function approvalLogEvents(
  lines: readonly string[],
  requestId: string,
): Array<Record<string, unknown>> {
  return lines.flatMap((line) => {
    const match = line.match(/^\[approval\] (\{.*\})$/u);
    if (!match) {
      return [];
    }
    try {
      const event = JSON.parse(match[1]) as Record<string, unknown>;
      return event.requestId === requestId ? [event] : [];
    } catch {
      return [];
    }
  });
}

function messageEvent(
  messageId: string,
  openId: string,
  text: string,
  chatType = "p2p",
  parentId?: string,
): Record<string, unknown> {
  return {
    sender: { sender_id: { open_id: openId } },
    message: {
      message_id: messageId,
      chat_id: `chat-${openId}`,
      chat_type: chatType,
      message_type: "text",
      content: JSON.stringify({ text }),
      ...(parentId ? { parent_id: parentId } : {}),
    },
  };
}

function groupMessageEvent(
  messageId: string,
  openId: string,
  chatId: string,
  text: string,
): Record<string, unknown> {
  return {
    sender: { sender_id: { open_id: openId } },
    message: {
      message_id: messageId,
      chat_id: chatId,
      chat_type: "group",
      message_type: "text",
      content: JSON.stringify({ text }),
    },
  };
}

test("only a private-chat owner can bind and duplicate messages are ignored", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-controller-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const feishu = new FakeFeishu();
    const codex = new FakeCodex();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      codex as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );
    const code = store.getPairingCode();
    assert.ok(code);

    await controller.handleFeishuMessage(
      messageEvent("bind-group", "owner", `绑定 ${code}`, "group"),
    );
    assert.equal(store.listBindings().length, 0);
    assert.match(feishu.replies.at(-1)?.text ?? "", /请先.*绑定/);

    await controller.handleFeishuMessage(
      messageEvent("bind-owner", "owner", `绑定 ${code}`),
    );
    assert.equal(store.isBound("owner"), true);
    await controller.handleFeishuMessage(
      messageEvent("bind-other", "other", `绑定 ${code}`),
    );
    assert.equal(store.isBound("other"), false);

    const repliesBeforeStatus = feishu.replies.length;
    const statusEvent = messageEvent("same-message", "owner", "状态");
    await controller.handleFeishuMessage(statusEvent);
    await controller.handleFeishuMessage(statusEvent);
    assert.equal(feishu.replies.length, repliesBeforeStatus + 1);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("creates one private session group and routes group replies to that session", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-session-group-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner(
      { openId: "owner", chatId: "chat-owner", chatType: "p2p", boundAt: new Date().toISOString() },
      code,
    );
    const sessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    const terminalId = "terminal-session-group";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "waiting",
      source: "startup",
      managedTerminalId: terminalId,
      managedByAssistant: true,
    });
    const feishu = new FakeFeishu();
    const terminals = new FakeManagedTerminals(terminalId, directory, sessionId);
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      terminals as unknown as ManagedTerminalRouter,
      undefined,
      controllerConfig(directory),
    );

    const approval = controller.handlePermissionHook({
      hook_event_name: "PermissionRequest",
      session_id: sessionId,
      turn_id: "group-create-turn",
      cwd: directory,
      model: "gpt-5",
      permission_mode: "default",
      tool_name: "shell_command",
      tool_input: { command: "echo test" },
      transcript_path: null,
      managed_terminal_id: terminalId,
    });
    for (let attempt = 0; attempt < 20 && feishu.createdGroups.length === 0; attempt += 1) {
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
    assert.equal(feishu.createdGroups.length, 1);
    const groupId = feishu.createdGroups[0]!.chatId;
    assert.equal(store.getSession(sessionId)?.feishuChatId, groupId);
    const approvalRecord = store.listApprovals().at(-1);
    assert.ok(approvalRecord);
    await controller.handleLocalApproval({
      requestId: approvalRecord.requestId,
      resolution: "allow",
    });
    await approval;

    await controller.handleFeishuMessage(
      groupMessageEvent("group-prompt", "owner", groupId, "请继续处理"),
    );
    assert.equal(terminals.sends.length, 1);
    assert.equal(terminals.sends[0]?.prompt, "请继续处理");

    await controller.handleFeishuMessage(
      groupMessageEvent("group-status-prompt", "owner", groupId, "状态"),
    );
    await controller.handleFeishuMessage(
      groupMessageEvent("group-help-prompt", "owner", groupId, "帮助"),
    );
    await controller.handleFeishuMessage(
      groupMessageEvent("group-bind-prompt", "owner", groupId, "绑定"),
    );
    await controller.handleFeishuMessage(
      groupMessageEvent("group-unbind-prompt", "owner", groupId, "解绑"),
    );
    assert.deepEqual(
      terminals.sends.slice(-4).map((item) => item.prompt),
      ["状态", "帮助", "绑定", "解绑"],
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("numbers same-name session groups and preserves the number on resume", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-group-number-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner(
      { openId: "owner", chatId: "chat-owner", chatType: "p2p", boundAt: new Date().toISOString() },
      code,
    );
    const firstSessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    const secondSessionId = "019faef0-d0bb-7703-af82-17ee9b45398c";
    await store.upsertSession({
      sessionId: firstSessionId,
      cwd: directory,
      status: "waiting",
      runtime: "codex",
      openedAt: "2026-08-04T01:00:00.000Z",
      managedTerminalId: "terminal-group-number-one",
      managedByAssistant: true,
    });
    await store.upsertSession({
      sessionId: secondSessionId,
      cwd: directory,
      status: "waiting",
      runtime: "codex",
      openedAt: "2026-08-04T02:00:00.000Z",
      managedTerminalId: "terminal-group-number-two",
      managedByAssistant: true,
    });
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );

    await controller.initializeSessionGroups();
    const baseName = `Codex｜${path.basename(directory)}`;
    assert.deepEqual(
      feishu.createdGroups.map((group) => group.name),
      [baseName, `${baseName}（2）`],
    );
    assert.equal(store.getSession(firstSessionId)?.feishuChatOrdinal, 1);
    assert.equal(store.getSession(secondSessionId)?.feishuChatOrdinal, 2);
    const secondChatId = store.getSession(secondSessionId)?.feishuChatId;

    await store.upsertSession({
      sessionId: secondSessionId,
      cwd: directory,
      status: "ended",
    });
    await store.upsertSession({
      sessionId: secondSessionId,
      cwd: directory,
      status: "waiting",
      runtime: "codex",
      managedTerminalId: "terminal-group-number-resume",
      managedByAssistant: true,
    });
    await controller.initializeSessionGroups();
    assert.equal(feishu.createdGroups.length, 2);
    assert.equal(store.getSession(secondSessionId)?.feishuChatId, secondChatId);
    assert.equal(store.getSession(secondSessionId)?.feishuChatOrdinal, 2);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("dissolves assistant session groups after one inactive week", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-group-cleanup-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const oldSessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    const recentSessionId = "019faef0-d0bb-7703-af82-17ee9b45397c";
    for (const sessionId of [oldSessionId, recentSessionId]) {
      await store.upsertSession({
        sessionId,
        cwd: directory,
        status: "ended",
        runtime: "codex",
        managedByAssistant: true,
      });
    }
    const now = Date.parse("2026-08-01T12:00:00.000Z");
    const oldAt = new Date(now - 8 * 24 * 60 * 60 * 1000).toISOString();
    const recentAt = new Date(now - 2 * 24 * 60 * 60 * 1000).toISOString();
    await store.setSessionFeishuChat(oldSessionId, {
      chatId: "old-session-chat",
      chatName: "old",
      createdAt: oldAt,
    });
    await store.setSessionFeishuChat(recentSessionId, {
      chatId: "recent-session-chat",
      chatName: "recent",
      createdAt: recentAt,
    });
    store.getSession(oldSessionId)!.lastSeenAt = oldAt;
    store.getSession(recentSessionId)!.lastSeenAt = oldAt;

    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      {
        ...controllerConfig(directory),
        sessionGroupInactiveMs: 7 * 24 * 60 * 60 * 1000,
      },
    );
    const result = await controller.cleanupInactiveSessionGroups(now);
    assert.deepEqual(result, { deleted: 1, failed: 0 });
    assert.deepEqual(feishu.deletedGroups, ["old-session-chat"]);
    assert.equal(store.getSession(oldSessionId)?.feishuChatId, undefined);
    assert.equal(store.getSession(recentSessionId)?.feishuChatId, "recent-session-chat");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a session group message auto-opens and resumes a closed managed session", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-auto-resume-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner(
      { openId: "owner", chatId: "chat-owner", chatType: "p2p", boundAt: new Date().toISOString() },
      code,
    );
    const sessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    const terminalId = "terminal-auto-resume";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "ended",
      runtime: "codex",
      managedTerminalId: terminalId,
      managedByAssistant: true,
    });
    await store.setSessionFeishuChat(sessionId, {
      chatId: "auto-resume-chat",
      chatName: "auto resume",
    });
    const feishu = new FakeFeishu();
    const terminals = new FakeManagedTerminals(
      terminalId,
      directory,
      sessionId,
      false,
    );
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      terminals as unknown as ManagedTerminalRouter,
      undefined,
      controllerConfig(directory),
    );

    await controller.handleFeishuMessage(
      groupMessageEvent(
        "auto-resume-message",
        "owner",
        "auto-resume-chat",
        "继续完成剩余工作",
      ),
    );
    assert.match(feishu.replies.at(-1)?.text ?? "", /自动恢复/);
    const claim = controller.handleRuntimeLaunchClaim() as {
      ok?: boolean;
      request?: {
        requestId: string;
        sessionId: string;
        runtime: string;
        cwd: string;
      };
    };
    assert.equal(claim.ok, true);
    assert.equal(claim.request?.sessionId, sessionId);
    assert.equal(claim.request?.runtime, "codex");
    assert.equal(claim.request?.cwd, directory);
    assert.ok(claim.request?.requestId);
    await controller.handleRuntimeLaunchComplete({
      requestId: claim.request!.requestId,
      success: true,
    });
    await controller.handleSessionStartHook({
      hook_event_name: "SessionStart",
      session_id: sessionId,
      cwd: directory,
      model: "gpt-5",
      permission_mode: "default",
      source: "resume",
      transcript_path: null,
      runtime: "codex",
      managed_terminal_id: terminalId,
      managed_terminal_elevated: false,
    });
    assert.deepEqual(terminals.sends, [
      { prompt: "继续完成剩余工作", submitMode: "steer" },
    ]);
    assert.equal(store.getSession(sessionId)?.status, "running");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a closed opencode group prompt waits for the resumed HTTP session", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-opencode-auto-resume-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner(
      { openId: "owner", chatId: "chat-owner", chatType: "p2p", boundAt: new Date().toISOString() },
      code,
    );
    const sessionId = "opencode-session-alpha";
    await store.upsertSession({
      sessionId,
      cwd: "C:/demo",
      status: "ended",
      source: "opencode",
      runtime: "opencode",
      managedByAssistant: true,
    });
    await store.setSessionFeishuChat(sessionId, {
      chatId: "opencode-auto-resume-chat",
      chatName: "opencode auto resume",
    });
    const opencode = new FakeOpenCodeManager();
    const controller = new BridgeController(
      store,
      new FakeFeishu() as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      opencode as unknown as OpenCodeManager,
      controllerConfig(directory),
    );

    await controller.handleFeishuMessage(
      groupMessageEvent(
        "opencode-auto-resume-message",
        "owner",
        "opencode-auto-resume-chat",
        "从原目录继续处理",
      ),
    );
    const claim = controller.handleRuntimeLaunchClaim() as {
      request?: { requestId: string; runtime: string; cwd: string };
    };
    assert.equal(claim.request?.runtime, "opencode");
    assert.equal(claim.request?.cwd, "C:/demo");
    await controller.handleRuntimeLaunchComplete({
      requestId: claim.request!.requestId,
      success: true,
    });
    opencode.connected = true;
    await controller.handleOpenCodeSessionCreated({
      id: sessionId,
      directory: "C:/demo",
    });
    assert.deepEqual(opencode.prompts, [
      { sessionId, prompt: "从原目录继续处理" },
    ]);
    assert.equal(store.getSession(sessionId)?.status, "running");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("OpenCode history remains routable metadata but only the foreground session is active", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-opencode-active-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    await store.upsertSession({
      sessionId: "opencode-session-alpha",
      cwd: "C:/demo",
      status: "waiting",
      source: "opencode",
      runtime: "opencode",
      managedByAssistant: true,
    });
    await store.upsertSession({
      sessionId: "opencode-session-history",
      cwd: "C:/demo",
      status: "waiting",
      source: "opencode",
      runtime: "opencode",
      managedByAssistant: true,
    });
    const opencode = new FakeOpenCodeManager();
    opencode.connected = true;
    opencode.routableSessions.add("opencode-session-history");
    const controller = new BridgeController(
      store,
      new FakeFeishu() as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      opencode as unknown as OpenCodeManager,
      controllerConfig(directory),
    );

    assert.equal(controller.health().activeSessions, 1);
    assert.ok(opencode.findInstanceBySession("opencode-session-history"));
    assert.equal(
      opencode.findActiveInstanceBySession("opencode-session-history"),
      undefined,
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("private Feishu commands create workspace projects and queue new runtimes", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-new-runtime-"));
  const dataDirectory = path.join(directory, "data");
  const workspaceRoot = path.join(directory, "workspace");
  try {
    const store = new BridgeStore(dataDirectory, { defaultWorkspaceRoot: workspaceRoot });
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner(
      { openId: "owner", chatId: "chat-owner", chatType: "p2p", boundAt: new Date().toISOString() },
      code,
    );
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(dataDirectory),
    );

    const cases: Array<{ command: string; runtime: string; projectName: string }> = [
      { command: "新建 codex 主项目", runtime: "codex", projectName: "主项目" },
      { command: "新建 Claude Code 内容工具", runtime: "claudecode", projectName: "内容工具" },
      { command: "新建 opencode 演示项目", runtime: "opencode", projectName: "演示项目" },
    ];
    for (const [index, item] of cases.entries()) {
      await controller.handleFeishuMessage(
        messageEvent(`new-runtime-${index}`, "owner", item.command),
      );
      const expectedCwd = await realpath(path.join(workspaceRoot, item.projectName));
      assert.equal((await stat(expectedCwd)).isDirectory(), true);
      const claim = controller.handleRuntimeLaunchClaim() as {
        request?: {
          requestId: string;
          kind: string;
          sessionId?: string;
          runtime: string;
          cwd: string;
          projectName: string;
          elevated: boolean;
        };
      };
      assert.equal(claim.request?.kind, "new");
      assert.equal(claim.request?.sessionId, undefined);
      assert.equal(claim.request?.runtime, item.runtime);
      assert.equal(claim.request?.cwd, expectedCwd);
      assert.equal(claim.request?.projectName, item.projectName);
      assert.equal(claim.request?.elevated, false);
      await controller.handleRuntimeLaunchComplete({
        requestId: claim.request!.requestId,
        success: true,
      });
    }

    await controller.handleFeishuMessage(
      messageEvent("new-runtime-existing", "owner", "新建 codex 主项目"),
    );
    assert.match(feishu.replies.at(-1)?.text ?? "", /已找到项目/);
    const existingClaim = controller.handleRuntimeLaunchClaim() as {
      request?: { requestId: string; cwd: string };
    };
    assert.equal(
      existingClaim.request?.cwd,
      await realpath(path.join(workspaceRoot, "主项目")),
    );
    await controller.handleRuntimeLaunchComplete({
      requestId: existingClaim.request!.requestId,
      success: true,
    });

    await controller.handleFeishuMessage(
      messageEvent("new-runtime-failure", "owner", "新建 codex 启动失败项目"),
    );
    const failedClaim = controller.handleRuntimeLaunchClaim() as {
      request?: { requestId: string };
    };
    await controller.handleRuntimeLaunchComplete({
      requestId: failedClaim.request!.requestId,
      success: false,
      error: "本机找不到 Codex CLI",
    });
    assert.match(feishu.replies.at(-1)?.text ?? "", /Codex 未启动：本机找不到 Codex CLI/);

    await controller.handleFeishuMessage(
      messageEvent("new-runtime-invalid", "owner", "新建 codex ../越界项目"),
    );
    assert.match(feishu.replies.at(-1)?.text ?? "", /项目名不正确/);
    assert.equal(
      (controller.handleRuntimeLaunchClaim() as { request?: unknown }).request,
      undefined,
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a failed session group create waits for an explicit desktop retry", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-group-retry-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner(
      { openId: "owner", chatId: "chat-owner", chatType: "p2p", boundAt: new Date().toISOString() },
      code,
    );
    const sessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    const terminalId = "terminal-session-group-retry";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "waiting",
      managedTerminalId: terminalId,
      managedByAssistant: true,
    });
    const feishu = new FakeFeishu();
    feishu.createGroupError = new Error("missing create chat permission");
    let attempts = 0;
    const originalCreate = feishu.createSessionGroup.bind(feishu);
    feishu.createSessionGroup = async (...args) => {
      attempts += 1;
      return await originalCreate(...args);
    };
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new FakeManagedTerminals(terminalId, directory, sessionId) as unknown as ManagedTerminalRouter,
      undefined,
      controllerConfig(directory),
    );

    await controller.initializeSessionGroups();
    assert.equal(attempts, 1);
    assert.match(store.getSession(sessionId)?.feishuChatError ?? "", /permission/);

    await controller.initializeSessionGroups();
    assert.equal(attempts, 1);

    feishu.createGroupError = undefined;
    const retry = await controller.handleSessionGroupRetry({ sessionId });
    assert.equal(retry.ok, true);
    assert.equal(attempts, 2);
    assert.ok(store.getSession(sessionId)?.feishuChatId);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("history removal hides managed and externally tracked sessions", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-history-hide-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const controller = new BridgeController(
      store,
      new FakeFeishu() as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );
    const managedSessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    await store.upsertSession({
      sessionId: managedSessionId,
      cwd: directory,
      status: "ended",
      managedByAssistant: true,
    });
    await store.upsertSession({
      sessionId: "external-history-session",
      cwd: directory,
      status: "ended",
      historyEligible: true,
    });
    await store.upsertSession({
      sessionId: "untracked-session",
      cwd: directory,
      status: "ended",
    });

    assert.equal(
      (await controller.handleSessionHistoryHide({})).ok,
      false,
    );
    assert.equal(
      (await controller.handleSessionHistoryHide({ sessionId: "untracked-session" })).ok,
      false,
    );
    assert.equal(
      (await controller.handleSessionHistoryHide({
        sessionId: "external-history-session",
      })).ok,
      true,
    );
    assert.equal(
      (await controller.handleSessionHistoryHide({ sessionId: managedSessionId })).ok,
      true,
    );
    assert.equal(store.getSession(managedSessionId)?.sessionId, managedSessionId);
    const health = controller.health() as { historySessions: unknown[] };
    assert.equal(health.historySessions.length, 0);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("/new card creates Codex, Claude Code, and OpenCode projects safely", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-new-card-"));
  const dataDirectory = path.join(directory, "data");
  const workspaceRoot = path.join(directory, "workspace");
  try {
    const store = new BridgeStore(dataDirectory, { defaultWorkspaceRoot: workspaceRoot });
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner(
      { openId: "owner", chatId: "chat-owner", chatType: "p2p", boundAt: new Date().toISOString() },
      code,
    );
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(dataDirectory),
    );
    const cases = [
      { runtime: "codex", projectName: "卡片-Codex" },
      { runtime: "claudecode", projectName: "卡片-Claude" },
      { runtime: "opencode", projectName: "卡片-OpenCode" },
    ];

    for (const [index, item] of cases.entries()) {
      const sourceMessageId = `slash-new-${index}`;
      await controller.handleFeishuMessage(
        messageEvent(sourceMessageId, "owner", "/new"),
      );
      const selectionMessage = feishu.cards.at(-1);
      assert.ok(selectionMessage);
      assert.equal(selectionMessage.chatId, "chat-owner");
      const selectionActions = findCardActions(
        selectionMessage.card,
        "runtime_new_select",
      );
      assert.equal(selectionActions.length, 3);
      assert.deepEqual(
        selectionActions.map((action) => action.runtime),
        ["codex", "claudecode", "opencode"],
      );
      const selectionAction = selectionActions.find(
        (action) => action.runtime === item.runtime,
      );
      assert.ok(selectionAction);

      const selected = await controller.handleCardAction({
        operator: { open_id: "owner" },
        context: {
          open_message_id: selectionMessage.messageId,
          open_chat_id: "chat-owner",
        },
        action: { value: selectionAction },
      });
      assert.equal(selected.toast.type, "info");
      assert.ok(selected.card);
      const submitAction = findCardAction(
        selected.card,
        "runtime_new_submit",
      );
      assert.ok(submitAction);
      assert.equal(submitAction.runtime, item.runtime);

      const submitted = await controller.handleCardAction({
        operator: { open_id: "owner" },
        context: {
          open_message_id: selectionMessage.messageId,
          open_chat_id: "chat-owner",
        },
        action: {
          value: submitAction,
          form_value: { project_name: item.projectName },
        },
      });
      assert.equal(submitted.toast.type, "success");
      assert.match(JSON.stringify(submitted.card), /已提交新建请求/);

      const expectedCwd = await realpath(path.join(workspaceRoot, item.projectName));
      assert.equal((await stat(expectedCwd)).isDirectory(), true);
      const claim = controller.handleRuntimeLaunchClaim() as {
        request?: {
          requestId: string;
          kind: string;
          runtime: string;
          cwd: string;
          projectName: string;
        };
      };
      assert.equal(claim.request?.kind, "new");
      assert.equal(claim.request?.runtime, item.runtime);
      assert.equal(claim.request?.cwd, expectedCwd);
      assert.equal(claim.request?.projectName, item.projectName);

      const duplicate = await controller.handleCardAction({
        operator: { open_id: "owner" },
        context: {
          open_message_id: selectionMessage.messageId,
          open_chat_id: "chat-owner",
        },
        action: {
          value: submitAction,
          form_value: { project_name: item.projectName },
        },
      });
      assert.equal(duplicate.toast.type, "warning");
      assert.match(duplicate.toast.content, /请勿重复点击/);

      await controller.handleRuntimeLaunchComplete({
        requestId: claim.request!.requestId,
        success: true,
      });
      assert.equal(
        (controller.handleRuntimeLaunchClaim() as { request?: unknown }).request,
        undefined,
      );
    }

    const cardCount = feishu.cards.length;
    await controller.handleFeishuMessage(
      messageEvent("slash-new-other", "other", "/new"),
    );
    assert.equal(feishu.cards.length, cardCount);
    assert.match(feishu.replies.at(-1)?.text ?? "", /只允许已设置的管理员账号操作/);

    await controller.handleFeishuMessage(
      messageEvent("slash-new-unauthorized-click", "owner", "/new"),
    );
    const protectedMessage = feishu.cards.at(-1);
    assert.ok(protectedMessage);
    const protectedAction = findCardAction(
      protectedMessage.card,
      "runtime_new_select",
    );
    assert.ok(protectedAction);
    const unauthorized = await controller.handleCardAction({
      operator: { open_id: "other" },
      context: {
        open_message_id: protectedMessage.messageId,
        open_chat_id: "chat-owner",
      },
      action: { value: protectedAction },
    });
    assert.equal(unauthorized.toast.type, "warning");
    assert.match(unauthorized.toast.content, /只有已绑定的管理员/);

    await controller.handleFeishuMessage(
      messageEvent("slash-new-invalid-card", "owner", "/new"),
    );
    const invalidMessage = feishu.cards.at(-1);
    assert.ok(invalidMessage);
    const invalidSelection = findCardAction(
      invalidMessage.card,
      "runtime_new_select",
    );
    assert.ok(invalidSelection);
    const invalidForm = await controller.handleCardAction({
      operator: { open_id: "owner" },
      context: {
        open_message_id: invalidMessage.messageId,
        open_chat_id: "chat-owner",
      },
      action: { value: invalidSelection },
    });
    const invalidSubmit = findCardAction(
      invalidForm.card,
      "runtime_new_submit",
    );
    assert.ok(invalidSubmit);
    const invalid = await controller.handleCardAction({
      operator: { open_id: "owner" },
      context: {
        open_message_id: invalidMessage.messageId,
        open_chat_id: "chat-owner",
      },
      action: {
        value: invalidSubmit,
        form_value: { project_name: "../越界项目" },
      },
    });
    assert.equal(invalid.toast.type, "error");
    assert.match(invalid.toast.content, /项目名不正确/);
    assert.equal(
      (controller.handleRuntimeLaunchClaim() as { request?: unknown }).request,
      undefined,
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("/new card reports a missing default workspace without queuing a launch", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-new-no-workspace-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner(
      { openId: "owner", chatId: "chat-owner", chatType: "p2p", boundAt: new Date().toISOString() },
      code,
    );
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );

    await controller.handleFeishuMessage(
      messageEvent("slash-new-no-workspace", "owner", "/new"),
    );
    const selectionMessage = feishu.cards.at(-1);
    assert.ok(selectionMessage);
    assert.match(JSON.stringify(selectionMessage.card), /尚未设置/);
    const selectionAction = findCardAction(
      selectionMessage.card,
      "runtime_new_select",
    );
    assert.ok(selectionAction);
    const selected = await controller.handleCardAction({
      operator: { open_id: "owner" },
      context: {
        open_message_id: selectionMessage.messageId,
        open_chat_id: "chat-owner",
      },
      action: { value: selectionAction },
    });
    const submitAction = findCardAction(
      selected.card,
      "runtime_new_submit",
    );
    assert.ok(submitAction);
    const submitted = await controller.handleCardAction({
      operator: { open_id: "owner" },
      context: {
        open_message_id: selectionMessage.messageId,
        open_chat_id: "chat-owner",
      },
      action: {
        value: submitAction,
        form_value: { project_name: "无工作区项目" },
      },
    });
    assert.equal(submitted.toast.type, "error");
    assert.match(submitted.toast.content, /尚未设置默认工作区/);
    assert.equal(
      (controller.handleRuntimeLaunchClaim() as { request?: unknown }).request,
      undefined,
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("history aliases keep the same session and Feishu group binding", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-history-alias-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const sessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "ended",
      managedByAssistant: true,
    });
    await store.setSessionFeishuChat(sessionId, {
      chatId: "history-alias-chat",
      chatName: "Codex｜old",
    });
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );

    const updated = await controller.handleSessionAliasUpdate({
      sessionId,
      alias: "归档会话",
    });
    assert.equal(updated.ok, true);
    assert.equal(store.getSession(sessionId)?.alias, "归档会话");
    assert.equal(store.getSession(sessionId)?.feishuChatId, "history-alias-chat");
    assert.deepEqual(feishu.renamedGroups.at(-1), {
      chatId: "history-alias-chat",
      name: "Codex｜归档会话",
    });

    const cleared = await controller.handleSessionAliasUpdate({
      sessionId,
      alias: null,
    });
    assert.equal(cleared.ok, true);
    assert.equal(store.getSession(sessionId)?.alias, undefined);
    assert.equal(store.getSession(sessionId)?.feishuChatId, "history-alias-chat");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("visible history aliases reserve their names until hidden", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-alias-conflict-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const firstSessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    const secondSessionId = "019faef0-d0bb-7703-af82-17ee9b45398c";
    for (const sessionId of [firstSessionId, secondSessionId]) {
      await store.upsertSession({
        sessionId,
        cwd: directory,
        status: "ended",
        managedByAssistant: true,
      });
    }
    await store.setSessionAlias(secondSessionId, "保留名");
    const controller = new BridgeController(
      store,
      new FakeFeishu() as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );

    const conflict = await controller.handleSessionAliasUpdate({
      sessionId: firstSessionId,
      alias: "保留名",
    });
    assert.equal(conflict.ok, false);
    assert.match(String(conflict.error), /已被会话/);

    await controller.handleSessionHistoryHide({ sessionId: secondSessionId });
    const reused = await controller.handleSessionAliasUpdate({
      sessionId: firstSessionId,
      alias: "保留名",
    });
    assert.equal(reused.ok, true);
    assert.equal(store.getSession(firstSessionId)?.alias, "保留名");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("concurrent alias updates cannot reserve the same visible name", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-alias-race-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const sessionIds = [
      "019faef0-d0bb-7703-af82-17ee9b45397b",
      "019faef0-d0bb-7703-af82-17ee9b45398c",
    ];
    for (const sessionId of sessionIds) {
      await store.upsertSession({
        sessionId,
        cwd: directory,
        status: "ended",
        managedByAssistant: true,
      });
    }
    const controller = new BridgeController(
      store,
      new FakeFeishu() as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );

    const results = await Promise.all(
      sessionIds.map((sessionId) =>
        controller.handleSessionAliasUpdate({ sessionId, alias: "并发别名" })
      ),
    );
    assert.equal(results.filter((result) => result.ok === true).length, 1);
    assert.equal(results.filter((result) => result.ok === false).length, 1);
    assert.match(String(results.find((result) => result.ok === false)?.error), /已被会话/);
    assert.equal(
      sessionIds.filter((sessionId) => store.getSession(sessionId)?.alias === "并发别名").length,
      1,
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("retry settings accept bounded integers and reject invalid values", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-settings-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const controller = new BridgeController(
      store,
      new FakeFeishu() as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );

    const saved = await controller.handleSettingsUpdate({
      workspaceRoot: directory,
      autoRetryErrors: true,
      retryMaxAttempts: 20,
      retryIntervalSeconds: 600,
      retryJitterSeconds: 120,
    });
    assert.equal(saved.ok, true);
    assert.deepEqual(store.getSettings(), {
      workspaceRoot: path.resolve(directory),
      notifyActivity: false,
      notifyUserPrompts: false,
      autoRetryErrors: true,
      retryMaxAttempts: 20,
      retryIntervalSeconds: 600,
      retryJitterSeconds: 120,
      autoApprove: false,
      notifyAutoApprovals: false,
    });

    for (const invalid of [
      { retryMaxAttempts: 0 },
      { retryMaxAttempts: 1.5 },
      { retryIntervalSeconds: 601 },
      { retryJitterSeconds: -1 },
      { retryJitterSeconds: "3" },
      { workspaceRoot: "relative-projects" },
    ]) {
      const result = await controller.handleSettingsUpdate(invalid);
      assert.equal(result.ok, false);
    }
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("health keeps active sessions in opening order when activity changes", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-order-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const olderId = "019faef0-d0bb-7703-af82-17ee9b453971";
    const newerId = "019faef0-d0bb-7703-af82-17ee9b453972";
    await store.upsertSession({
      sessionId: olderId,
      cwd: path.join(directory, "older"),
      status: "waiting",
      source: "startup",
      clientProcessId: process.pid,
      openedAt: "2026-07-31T08:00:00.000Z",
    });
    await store.upsertSession({
      sessionId: newerId,
      cwd: path.join(directory, "newer"),
      status: "waiting",
      source: "startup",
      clientProcessId: process.pid,
      openedAt: "2026-07-31T09:00:00.000Z",
    });
    await store.upsertSession({
      sessionId: olderId,
      cwd: path.join(directory, "older"),
      status: "running",
      source: "startup",
      clientProcessId: process.pid,
    });
    const controller = new BridgeController(
      store,
      new FakeFeishu() as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );

    const sessions = (controller.health() as {
      sessions: Array<{ sessionId: string }>;
    }).sessions;
    assert.deepEqual(sessions.map((item) => item.sessionId), [olderId, newerId]);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("an external session rejects ordinary Feishu replies without a background resume", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-lock-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner(
      {
        openId: "owner",
        chatId: "chat-owner",
        chatType: "p2p",
        boundAt: new Date().toISOString(),
      },
      code,
    );
    await store.upsertSession({
      sessionId: "019faef0-d0bb-7703-af82-17ee9b45397b",
      cwd: directory,
      status: "waiting",
      source: "resume",
      clientProcessId: process.pid,
      managedByAssistant: true,
    });
    await store.setSessionFeishuChat("019faef0-d0bb-7703-af82-17ee9b45397b", {
      chatId: "external-session-chat",
      chatName: "external session",
    });
    const feishu = new FakeFeishu();
    const codex = new FakeCodex();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      codex as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );

    await Promise.all([
      controller.handleFeishuMessage(
        groupMessageEvent("prompt-1", "owner", "external-session-chat", "第一条"),
      ),
      controller.handleFeishuMessage(
        groupMessageEvent("prompt-2", "owner", "external-session-chat", "第二条"),
      ),
    ]);
    assert.equal(codex.resumeCount, 0);
    assert.equal(codex.prompts.length, 0);
    assert.deepEqual(
      feishu.replies.map((item) => item.text),
      [
        "Codex 未接收：这个窗口不是由 AI CLI 飞书助手打开，不能从飞书回复。请回到原窗口继续。",
        "Codex 未接收：这个窗口不是由 AI CLI 飞书助手打开，不能从飞书回复。请回到原窗口继续。",
      ],
    );
    assert.equal(controller.health().queuedPrompts, 0);
    assert.equal(
      (controller.handleRuntimeLaunchClaim() as { request?: unknown }).request,
      undefined,
    );
    assert.equal(
      store.getSession("019faef0-d0bb-7703-af82-17ee9b45397b")?.status,
      "waiting",
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a managed session steers by default and queues explicitly", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-steer-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner(
      {
        openId: "owner",
        chatId: "chat-owner",
        chatType: "p2p",
        boundAt: new Date().toISOString(),
      },
      code,
    );
    const sessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "running",
      source: "startup",
      managedTerminalId: "terminal999",
    });
    const terminals = new FakeManagedTerminals("terminal999", directory, sessionId);
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      terminals as unknown as ManagedTerminalRouter,
      undefined,
      controllerConfig(directory),
    );

    await controller.handleFeishuMessage(messageEvent("steer-1", "owner", "补充这个条件"));
    await controller.handleFeishuMessage(
      messageEvent("queue-1", "owner", "排队 下一轮再运行测试"),
    );

    assert.deepEqual(terminals.sends, [
      { prompt: "补充这个条件", submitMode: "steer" },
      { prompt: "下一轮再运行测试", submitMode: "queue" },
    ]);
    assert.deepEqual(
      feishu.replies.map((item) => item.text),
      ["Codex 已接收。", "Codex 已接收。"],
    );
    assert.equal(controller.health().queuedPrompts, 1);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("local and Feishu approval resolutions share one atomic result", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-approval-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner(
      {
        openId: "owner",
        chatId: "chat-owner",
        chatType: "p2p",
        boundAt: new Date().toISOString(),
      },
      code,
    );
    const sessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "running",
      source: "startup",
      clientProcessId: process.pid,
    });
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );

    const hookResultPromise = controller.handlePermissionHook({
      hook_event_name: "PermissionRequest",
      session_id: sessionId,
      turn_id: "turn-approval-1",
      cwd: directory,
      model: "gpt-5",
      permission_mode: "default",
      tool_name: "Bash",
      tool_input: {
        command: "npm test",
        description: "运行测试",
      },
      transcript_path: null,
    });
    while (feishu.cards.length === 0) {
      await new Promise<void>((resolve) => setImmediate(resolve));
    }

    const health = controller.health() as {
      pendingApprovals: number;
      approvals: Array<{ requestId: string; status: string }>;
    };
    assert.equal(health.pendingApprovals, 1);
    assert.equal(health.approvals[0]?.status, "pending");
    const requestId = health.approvals[0]!.requestId;

    const [localResult, competingResult] = await Promise.all([
      controller.handleLocalApproval({ requestId, resolution: "allow" }),
      controller.handleLocalApproval({ requestId, resolution: "deny" }),
    ]);
    const results = [localResult, competingResult] as Array<{
      ok?: boolean;
      alreadyResolved?: boolean;
      resolution?: string;
    }>;
    assert.ok(results.every((result) => result.ok === true));
    assert.equal(results.filter((result) => result.alreadyResolved !== true).length, 1);

    const hookResult = await hookResultPromise;
    assert.match(JSON.stringify(hookResult), /"behavior":"allow"/);
    const resolvedHealth = controller.health() as {
      pendingApprovals: number;
      approvals: Array<{ requestId: string; status: string; resolution: string }>;
    };
    assert.equal(resolvedHealth.pendingApprovals, 0);
    const resolvedApproval = resolvedHealth.approvals.find(
      (item) => item.requestId === requestId,
    );
    assert.equal(resolvedApproval?.status, "resolved");
    assert.equal(resolvedApproval?.resolution, "allow");
    for (let attempt = 0; attempt < 30 && feishu.patchedCards.length === 0; attempt += 1) {
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
    assert.ok(feishu.patchedCards.some((item) => item.messageId === feishu.cards[0]!.messageId));
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a disconnected Codex permission hook invalidates its Feishu approval card", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-hook-disconnect-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner(
      {
        openId: "owner",
        chatId: "chat-owner",
        chatType: "p2p",
        boundAt: new Date().toISOString(),
      },
      code,
    );
    const sessionId = "codex-hook-disconnected-before-feishu-click";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "running",
      source: "startup",
      runtime: "codex",
      clientProcessId: process.pid,
    });
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );
    const abortController = new AbortController();
    const hookResultPromise = controller.handlePermissionHook(
      {
        hook_event_name: "PermissionRequest",
        session_id: sessionId,
        turn_id: "turn-disconnected",
        cwd: directory,
        model: "gpt-5",
        permission_mode: "default",
        tool_name: "shell_command",
        tool_input: { command: "npm test" },
        transcript_path: null,
        runtime: "codex",
      },
      abortController.signal,
    );
    while (feishu.cards.length === 0) {
      await new Promise<void>((resolve) => setImmediate(resolve));
    }
    const approval = store.listApprovals().at(-1);
    assert.ok(approval);

    abortController.abort();
    assert.deepEqual(await hookResultPromise, {});
    assert.equal(store.getApproval(approval.requestId)?.status, "resolved");
    assert.equal(store.getApproval(approval.requestId)?.resolution, "local");
    for (let attempt = 0; attempt < 30 && feishu.patchedCards.length === 0; attempt += 1) {
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
    const patchedCard = feishu.patchedCards.find(
      (item) => item.messageId === feishu.cards[0]?.messageId,
    )?.card;
    assert.ok(patchedCard);
    assert.match(JSON.stringify(patchedCard), /已转回电脑端/);
    assert.doesNotMatch(JSON.stringify(patchedCard), /批准一次/);

    const staleClick = await controller.handleCardAction({
      operator: { open_id: "owner" },
      action: {
        value: {
          action: "approval_allow",
          requestId: approval.requestId,
          sessionId,
        },
      },
    });
    assert.equal(staleClick.toast.type, "warning");
    assert.match(staleClick.toast.content, /已经处理或失效/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("manual approvals stay Feishu-first and fall back to PC when delivery fails", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-feishu-first-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner({
      openId: "owner",
      chatId: "chat-owner",
      chatType: "p2p",
      boundAt: new Date().toISOString(),
    }, code);
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );

    const firstHook = controller.handlePermissionHook({
      hook_event_name: "PermissionRequest",
      session_id: "feishu-first-session",
      turn_id: "feishu-first-turn",
      cwd: directory,
      model: "gpt-5",
      permission_mode: "default",
      tool_name: "Bash",
      tool_input: { command: "npm test" },
      transcript_path: null,
    });
    while (feishu.cards.length === 0) {
      await new Promise<void>((resolve) => setImmediate(resolve));
    }
    const firstApproval = store.listApprovals()[0];
    assert.ok(firstApproval);
    const initialHealth = controller.health() as {
      pendingApprovals: number;
      pendingDesktopApprovals: number;
      approvals: Array<{ requestId: string; desktopApprovalRequested: boolean }>;
    };
    assert.equal(initialHealth.pendingApprovals, 1);
    assert.equal(initialHealth.pendingDesktopApprovals, 0);
    assert.equal(initialHealth.approvals[0]?.desktopApprovalRequested, false);
    assert.match(JSON.stringify(feishu.cards[0]?.card), /"action":"approval_desktop"/);

    const transferResult = await controller.handleCardAction({
      operator: { open_id: "owner" },
      action: {
        value: {
          action: "approval_desktop",
          requestId: firstApproval.requestId,
          sessionId: firstApproval.sessionId,
        },
      },
    });
    assert.equal(transferResult.toast.type, "success");
    assert.equal(store.getApproval(firstApproval.requestId)?.status, "pending");
    assert.equal(
      (controller.health() as { pendingDesktopApprovals: number }).pendingDesktopApprovals,
      1,
    );
    assert.match(JSON.stringify(feishu.patchedCards.at(-1)?.card), /已转回 PC 审批/);

    await controller.handleFeishuMessage(
      messageEvent(
        "approval-local-text",
        "owner",
        "本机确认",
        "p2p",
        feishu.cards[0]!.messageId,
      ),
    );
    assert.match(feishu.replies.at(-1)?.text ?? "", /已转回 PC 审批/);
    await controller.handleLocalApproval({
      requestId: firstApproval.requestId,
      resolution: "allow",
    });
    assert.match(JSON.stringify(await firstHook), /"behavior":"allow"/);

    feishu.failCardSendAttempts.add(2);
    const fallbackHook = controller.handlePermissionHook({
      hook_event_name: "PermissionRequest",
      session_id: "desktop-fallback-session",
      turn_id: "desktop-fallback-turn",
      cwd: directory,
      model: "gpt-5",
      permission_mode: "default",
      tool_name: "Bash",
      tool_input: { command: "npm test" },
      transcript_path: null,
    });
    while (
      (controller.health() as { pendingDesktopApprovals: number })
          .pendingDesktopApprovals !== 1
    ) {
      await new Promise<void>((resolve) => setImmediate(resolve));
    }
    const fallbackApproval = store
      .listApprovals()
      .find((approval) => approval.sessionId === "desktop-fallback-session");
    assert.equal(fallbackApproval?.desktopApprovalRequested, true);
    await controller.handleLocalApproval({
      requestId: fallbackApproval!.requestId,
      resolution: "deny",
    });
    assert.match(JSON.stringify(await fallbackHook), /"behavior":"deny"/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("closing the controller returns pending approval and input hooks to the local CLI", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-close-hooks-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner({
      openId: "owner",
      chatId: "chat-owner",
      chatType: "p2p",
      boundAt: new Date().toISOString(),
    }, code);
    const controller = new BridgeController(
      store,
      new FakeFeishu() as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );

    const approvalHook = controller.handlePermissionHook({
      hook_event_name: "PermissionRequest",
      session_id: "shutdown-approval-session",
      turn_id: "shutdown-approval-turn",
      cwd: directory,
      model: "gpt-5",
      permission_mode: "default",
      tool_name: "Bash",
      tool_input: { command: "npm test" },
      transcript_path: null,
    });
    const inputHook = controller.handleRequestUserInputHook({
      hook_event_name: "PreToolUse",
      session_id: "shutdown-input-session",
      turn_id: "shutdown-input-turn",
      cwd: directory,
      model: "gpt-5",
      tool_name: "request_user_input",
      tool_input: {
        questions: [
          {
            header: "方式",
            id: "shutdown_mode",
            question: "选择处理方式",
            options: [
              { label: "继续", description: "继续执行" },
              { label: "停止", description: "停止执行" },
            ],
          },
        ],
      },
    });

    while (
      controller.health().pendingApprovals !== 1 ||
      controller.health().pendingInputs !== 1
    ) {
      await new Promise<void>((resolve) => setImmediate(resolve));
    }
    await controller.close();

    assert.deepEqual(await approvalHook, {});
    assert.deepEqual(await inputHook, {});
    assert.equal(controller.health().pendingApprovals, 0);
    assert.equal(controller.health().pendingInputs, 0);
    assert.equal(store.listApprovals()[0]?.resolution, "local");
    assert.equal(store.getSession("shutdown-approval-session")?.status, "local_approval");
    assert.equal(store.getSession("shutdown-input-session")?.status, "waiting");
    await store.close();
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("an approval completed in Feishu is visible to the desktop and logs its source", async (t) => {
  const approvalLogs: string[] = [];
  t.mock.method(console, "log", (...args: unknown[]) => {
    approvalLogs.push(args.map((value) => String(value)).join(" "));
  });
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-approval-sync-"));
  const approvalLogPath = path.join(directory, "approval-events.log");
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner(
      {
        openId: "owner",
        chatId: "chat-owner",
        chatType: "p2p",
        boundAt: new Date().toISOString(),
      },
      code,
    );
    const sessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "running",
      source: "startup",
      clientProcessId: process.pid,
    });
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      { ...controllerConfig(directory), approvalLogPath },
    );

    const hookResultPromise = controller.handlePermissionHook({
      hook_event_name: "PermissionRequest",
      session_id: sessionId,
      turn_id: "turn-approval-2",
      cwd: directory,
      model: "gpt-5",
      permission_mode: "default",
      tool_name: "apply_patch",
      tool_input: { command: "*** Begin Patch" },
      transcript_path: null,
    });
    while (feishu.cards.length === 0) {
      await new Promise<void>((resolve) => setImmediate(resolve));
    }
    const health = controller.health() as {
      approvals: Array<{ requestId: string }>;
    };
    const requestId = health.approvals[0]!.requestId;
    while (
      !approvalLogEvents(approvalLogs, requestId).some(
        (event) => event.event === "notification_sent",
      )
    ) {
      await new Promise<void>((resolve) => setImmediate(resolve));
    }

    const actionResult = await controller.handleCardAction({
      operator: { open_id: "owner" },
      action: {
        value: {
          action: "approval_deny",
          requestId,
          sessionId,
        },
      },
    });
    assert.equal(actionResult.toast.type, "success");
    const hookResult = await hookResultPromise;
    assert.match(JSON.stringify(hookResult), /"behavior":"deny"/);

    const events = approvalLogEvents(approvalLogs, requestId);
    assert.ok(events.some((event) => event.event === "requested"));
    assert.ok(events.some((event) => event.event === "notification_sent"));
    const resolvedEvent = events.find((event) => event.event === "resolved");
    assert.equal(resolvedEvent?.decisionSource, "feishu_card");
    assert.equal(resolvedEvent?.notificationSentBeforeResolution, true);
    assert.equal(typeof resolvedEvent?.notificationFirstSentAt, "string");
    assert.equal(typeof resolvedEvent?.elapsedSinceNotificationMs, "number");
    assert.ok((resolvedEvent?.elapsedSinceNotificationMs as number) >= 0);
    assert.equal(resolvedEvent?.notificationCount, 1);

    const desktopResult = await controller.handleLocalApproval({
      requestId,
      resolution: "allow",
    }) as {
      ok?: boolean;
      alreadyResolved?: boolean;
      resolution?: string;
    };
    assert.equal(desktopResult.ok, true);
    assert.equal(desktopResult.alreadyResolved, true);
    assert.equal(desktopResult.resolution, "deny");
    await controller.close();
    const persistedLog = await readFile(approvalLogPath, "utf8");
    assert.match(persistedLog, new RegExp(requestId, "u"));
    assert.match(persistedLog, /"event":"notification_sent"/u);
    assert.match(persistedLog, /"decisionSource":"feishu_card"/u);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("approval audit logs rotate within the configured size and backup limit", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-approval-rotation-"));
  const approvalLogPath = path.join(directory, "approval-events.log");
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const session = await store.upsertSession({
      sessionId: "session-approval-log-rotation",
      cwd: directory,
      status: "waiting",
    });
    const controller = new BridgeController(
      store,
      new FakeFeishu() as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      {
        ...controllerConfig(directory),
        approvalLogPath,
        approvalLogMaxBytes: 350,
        approvalLogMaxBackups: 2,
      },
    );
    const approval: ApprovalRecord = {
      requestId: "approval-log-rotation",
      sessionId: session.sessionId,
      turnId: "turn-log-rotation",
      cwd: directory,
      toolName: "shell_command",
      toolPreview: "npm test",
      createdAt: new Date().toISOString(),
      expiresAt: new Date(Date.now() + 60_000).toISOString(),
      status: "pending",
      messageIds: [],
    };
    const internal = controller as unknown as {
      approvals: {
        logEvent: (
          event: string,
          session: SessionRecord,
          approval: ApprovalRecord,
          details?: Record<string, unknown>,
        ) => void;
      };
    };
    for (let index = 0; index < 10; index += 1) {
      internal.approvals.logEvent(`rotation_${index}`, session, approval, {
        payload: "x".repeat(160),
      });
    }
    await controller.close();

    assert.match(await readFile(approvalLogPath, "utf8"), /rotation_9/);
    assert.match(await readFile(`${approvalLogPath}.1`, "utf8"), /rotation_8/);
    assert.match(await readFile(`${approvalLogPath}.2`, "utf8"), /rotation_7/);
    await assert.rejects(stat(`${approvalLogPath}.3`), { code: "ENOENT" });
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("automatic Codex approval is silent by default and logs an automatic decision", async (t) => {
  const approvalLogs: string[] = [];
  t.mock.method(console, "log", (...args: unknown[]) => {
    approvalLogs.push(args.map((value) => String(value)).join(" "));
  });
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-auto-approval-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    await store.updateSettings({ autoApprove: true });
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner({
      openId: "owner",
      chatId: "chat-owner",
      chatType: "p2p",
      boundAt: new Date().toISOString(),
    }, code);
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );
    const result = await controller.handlePermissionHook({
      hook_event_name: "PermissionRequest",
      session_id: "019faef0-d0bb-7703-af82-17ee9b45397b",
      turn_id: "turn-auto-approval",
      cwd: directory,
      model: "gpt-5",
      permission_mode: "default",
      tool_name: "Bash",
      tool_input: { command: "npm test" },
      transcript_path: null,
    });
    assert.match(JSON.stringify(result), /"behavior":"allow"/);
    const health = controller.health() as {
      pendingApprovals: number;
      approvals: Array<{ resolution?: string }>;
    };
    assert.equal(health.pendingApprovals, 0);
    assert.equal(health.approvals[0]?.resolution, "allow");
    assert.equal(feishu.cards.length, 0);
    assert.equal(feishu.patchedCards.length, 0);
    const requestId = store.listApprovals()[0]!.requestId;
    const events = approvalLogEvents(approvalLogs, requestId);
    assert.ok(events.some((event) => event.event === "automatic_attempt"));
    const resolvedEvent = events.find((event) => event.event === "resolved");
    assert.equal(resolvedEvent?.decisionSource, "automatic");
    assert.equal(resolvedEvent?.notificationSentBeforeResolution, null);
    assert.equal(resolvedEvent?.elapsedSinceNotificationMs, null);
    assert.ok(events.some((event) => event.event === "automatic_completed"));
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("automatic Claude Code approval uses the same silent bridge flow", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-claude-auto-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    await store.updateSettings({ autoApprove: true });
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );
    const result = await controller.handlePermissionHook({
      hook_event_name: "PermissionRequest",
      session_id: "claude-session-auto",
      turn_id: "claude-turn-auto",
      cwd: directory,
      model: "claude-sonnet-4-5",
      permission_mode: "default",
      tool_name: "Bash",
      tool_input: { command: "npm test" },
      transcript_path: null,
      runtime: "claudecode",
    });
    assert.match(JSON.stringify(result), /"behavior":"allow"/);
    assert.equal(store.listApprovals()[0]?.resolution, "allow");
    assert.equal(feishu.cards.length, 0);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("high-risk commands remain manual when automatic approval is enabled", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-risk-approval-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    await store.updateSettings({ autoApprove: true });
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner({
      openId: "owner",
      chatId: "chat-owner",
      chatType: "p2p",
      boundAt: new Date().toISOString(),
    }, code);
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );
    const resultPromise = controller.handlePermissionHook({
      hook_event_name: "PermissionRequest",
      session_id: "claude-session-high-risk",
      turn_id: "claude-turn-high-risk",
      cwd: directory,
      model: "claude-sonnet-4-5",
      permission_mode: "default",
      tool_name: "Bash",
      tool_input: { command: "rm -rf build" },
      transcript_path: null,
      runtime: "claudecode",
    });
    while (feishu.cards.length === 0) {
      await new Promise<void>((resolve) => setImmediate(resolve));
    }
    const approval = store.listApprovals()[0];
    assert.ok(approval);
    assert.equal(approval.status, "pending");
    assert.equal(approval.requiresManualApproval, true);
    assert.equal(approval.riskLevel, "high");
    assert.match(JSON.stringify(feishu.cards[0]?.card), /高风险操作需要确认/);

    await controller.handleLocalApproval({
      requestId: approval.requestId,
      resolution: "allow",
    });
    const result = await resultPromise;
    assert.match(JSON.stringify(result), /"behavior":"allow"/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("high-risk approval timeout returns control to the local runtime", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-risk-timeout-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    await store.updateSettings({ autoApprove: true });
    const controller = new BridgeController(
      store,
      new FakeFeishu() as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      { ...controllerConfig(directory), approvalTimeoutMs: 30 },
    );
    const result = await controller.handlePermissionHook({
      hook_event_name: "PermissionRequest",
      session_id: "codex-session-high-risk-timeout",
      turn_id: "codex-turn-high-risk-timeout",
      cwd: directory,
      model: "gpt-5",
      permission_mode: "default",
      tool_name: "shell_command",
      tool_input: { command: "git push origin main" },
      transcript_path: null,
    });
    assert.deepEqual(result, {});
    const approval = store.listApprovals()[0];
    assert.equal(approval?.riskLevel, "high");
    assert.equal(approval?.resolution, "timeout");
    assert.equal(store.getSession("codex-session-high-risk-timeout")?.status, "local_approval");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("automatic approval can send only a resolved audit card", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-auto-audit-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    await store.updateSettings({
      autoApprove: true,
      notifyAutoApprovals: true,
    });
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner({
      openId: "owner",
      chatId: "chat-owner",
      chatType: "p2p",
      boundAt: new Date().toISOString(),
    }, code);
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );
    await controller.handlePermissionHook({
      hook_event_name: "PermissionRequest",
      session_id: "codex-session-auto-audit",
      turn_id: "turn-auto-audit",
      cwd: directory,
      model: "gpt-5",
      permission_mode: "default",
      tool_name: "Bash",
      tool_input: { command: "npm test" },
      transcript_path: null,
    });
    assert.equal(feishu.cards.length, 1);
    assert.match(JSON.stringify(feishu.cards[0]?.card), /审批已处理/);
    assert.doesNotMatch(JSON.stringify(feishu.cards[0]?.card), /批准一次/);
    assert.equal(feishu.patchedCards.length, 0);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("request_user_input can be answered by replying to the Feishu card", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-input-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner(
      {
        openId: "owner",
        chatId: "chat-owner",
        chatType: "p2p",
        boundAt: new Date().toISOString(),
      },
      code,
    );
    const sessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "running",
      source: "startup",
      clientProcessId: process.pid,
    });
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );

    const hookResultPromise = controller.handleRequestUserInputHook({
      hook_event_name: "PreToolUse",
      session_id: sessionId,
      turn_id: "turn-input-1",
      cwd: directory,
      model: "gpt-5",
      tool_name: "request_user_input",
      tool_input: {
        questions: [
          {
            header: "发布方式",
            id: "publish_mode",
            question: "选择发布方式",
            options: [
              { label: "仅构建", description: "只生成文件" },
              { label: "构建并发布", description: "生成并发布" },
            ],
          },
        ],
      },
    });
    while (feishu.cards.length === 0) {
      await new Promise<void>((resolve) => setImmediate(resolve));
    }
    const questionCardId = feishu.cards[0]!.messageId;
    await controller.handleFeishuMessage(
      messageEvent("input-answer", "owner", "2", "p2p", questionCardId),
    );
    const hookResult = await hookResultPromise;
    assert.match(JSON.stringify(hookResult), /构建并发布/);
    assert.match(JSON.stringify(hookResult), /permissionDecision/);
    assert.equal(controller.health().pendingInputs, 0);
    assert.ok(feishu.patchedCards.some((item) => item.messageId === questionCardId));
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("multiple request_user_input questions use separate clickable cards", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-input-cards-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner({
      openId: "owner",
      chatId: "chat-owner",
      chatType: "p2p",
      boundAt: new Date().toISOString(),
    }, code);
    const sessionId = "codex-session-separate-input-cards";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "running",
      runtime: "codex",
      clientProcessId: process.pid,
    });
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as WorkBuddyRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );

    const hookResultPromise = controller.handleRequestUserInputHook({
      hook_event_name: "PreToolUse",
      session_id: sessionId,
      turn_id: "turn-separate-input-cards",
      cwd: directory,
      model: "gpt-5",
      tool_name: "request_user_input",
      tool_input: {
        questions: [
          {
            header: "发布方式",
            id: "publish",
            question: "如何发布？",
            options: [
              { label: "仅构建", description: "只生成文件" },
              { label: "构建并发布", description: "生成并发布" },
            ],
            custom: false,
          },
          {
            header: "通知范围",
            id: "notify",
            question: "通知谁？",
            options: [
              { label: "团队", description: "通知团队" },
              { label: "负责人", description: "只通知负责人" },
            ],
            custom: false,
          },
        ],
      },
    });
    while (feishu.cards.length < 2) {
      await new Promise<void>((resolve) => setImmediate(resolve));
    }
    assert.equal(feishu.cards.length, 2);
    assert.doesNotMatch(JSON.stringify(feishu.cards[0]?.card), /通知范围/);
    assert.doesNotMatch(JSON.stringify(feishu.cards[1]?.card), /发布方式/);

    const firstAction = findCardAction(feishu.cards[0]!.card, "input_answer", "构建并发布");
    const secondAction = findCardAction(feishu.cards[1]!.card, "input_answer", "负责人");
    assert.ok(firstAction);
    assert.ok(secondAction);
    const firstResult = await controller.handleCardAction({
      operator: { open_id: "owner" },
      action: { value: JSON.stringify(firstAction) },
    });
    assert.equal(firstResult.toast.type, "success");
    assert.equal(controller.health().pendingInputs, 1);
    assert.match(JSON.stringify(feishu.patchedCards.at(-1)?.card), /已记录/);

    const secondResult = await controller.handleCardAction({
      operator: { open_id: "owner" },
      action: { value: JSON.stringify(secondAction) },
    });
    assert.equal(secondResult.toast.type, "success");
    const hookResult = await hookResultPromise;
    assert.match(JSON.stringify(hookResult), /构建并发布/);
    assert.match(JSON.stringify(hookResult), /负责人/);
    assert.equal(controller.health().pendingInputs, 0);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("request_user_input falls back locally when any question has no delivered card", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-input-partial-send-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner({
      openId: "owner",
      chatId: "chat-owner",
      chatType: "p2p",
      boundAt: new Date().toISOString(),
    }, code);
    const sessionId = "codex-session-partial-input-cards";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "running",
      runtime: "codex",
      clientProcessId: process.pid,
    });
    const feishu = new FakeFeishu();
    feishu.failCardSendAttempts.add(2);
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );

    const result = await controller.handleRequestUserInputHook({
      hook_event_name: "PreToolUse",
      session_id: sessionId,
      turn_id: "turn-partial-input-cards",
      cwd: directory,
      model: "gpt-5",
      tool_name: "request_user_input",
      tool_input: {
        questions: [
          {
            header: "问题一",
            id: "q1",
            question: "选择一？",
            options: [{ label: "一", description: "一" }],
            custom: false,
          },
          {
            header: "问题二",
            id: "q2",
            question: "选择二？",
            options: [{ label: "二", description: "二" }],
            custom: false,
          },
          {
            header: "问题三",
            id: "q3",
            question: "选择三？",
            options: [{ label: "三", description: "三" }],
            custom: false,
          },
        ],
      },
    });

    assert.deepEqual(result, {});
    assert.equal(feishu.cards.length, 2);
    assert.equal(controller.health().pendingInputs, 0);
    for (let attempt = 0; attempt < 20 && feishu.patchedCards.length < 2; attempt += 1) {
      await new Promise((resolve) => setTimeout(resolve, 5));
    }
    assert.equal(feishu.patchedCards.length, 2);
    assert.ok(feishu.patchedCards.every((item) =>
      JSON.stringify(item.card).includes("已转回电脑端")
    ));
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("multi-choice request_user_input cards toggle and submit selected options", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-input-multi-cards-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner({
      openId: "owner",
      chatId: "chat-owner",
      chatType: "p2p",
      boundAt: new Date().toISOString(),
    }, code);
    const sessionId = "codex-session-multi-input-cards";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "running",
      runtime: "codex",
      clientProcessId: process.pid,
    });
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as WorkBuddyRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );
    const hookResultPromise = controller.handleRequestUserInputHook({
      hook_event_name: "PreToolUse",
      session_id: sessionId,
      turn_id: "turn-multi-input-cards",
      cwd: directory,
      model: "gpt-5",
      tool_name: "request_user_input",
      tool_input: {
        questions: [{
          header: "范围",
          id: "scope",
          question: "选择范围",
          options: [
            { label: "代码", description: "源代码" },
            { label: "测试", description: "自动化测试" },
            { label: "文档", description: "项目说明" },
          ],
          multiple: true,
          custom: false,
        }],
      },
    });
    while (feishu.cards.length < 1) {
      await new Promise<void>((resolve) => setImmediate(resolve));
    }
    const card = feishu.cards[0]!;
    const codeAction = findCardAction(card.card, "input_toggle", "代码");
    const docsAction = findCardAction(card.card, "input_toggle", "文档");
    const submitAction = findCardAction(card.card, "input_submit");
    assert.ok(codeAction);
    assert.ok(docsAction);
    assert.ok(submitAction);
    await controller.handleCardAction({
      operator: { open_id: "owner" },
      action: { value: JSON.stringify(codeAction) },
    });
    await controller.handleCardAction({
      operator: { open_id: "owner" },
      action: { value: JSON.stringify(docsAction) },
    });
    assert.match(JSON.stringify(feishu.patchedCards.at(-1)?.card), /已选 2 项/);
    const submitted = await controller.handleCardAction({
      operator: { open_id: "owner" },
      action: { value: JSON.stringify(submitAction) },
    });
    assert.equal(submitted.toast.type, "success");
    const hookResult = await hookResultPromise;
    assert.match(JSON.stringify(hookResult), /代码/);
    assert.match(JSON.stringify(hookResult), /文档/);
    assert.equal(controller.health().pendingInputs, 0);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("multi-recipient selections remain isolated per Feishu chat", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-input-recipients-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner({
      openId: "owner",
      chatId: "chat-owner",
      chatType: "p2p",
      boundAt: new Date().toISOString(),
    }, code);
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );
    const internal = controller as unknown as {
      sessionGroups: {
        notificationRecipients: () => Promise<Array<{ chatId: string }>>;
      };
    };
    internal.sessionGroups.notificationRecipients = async () => [
      { chatId: "chat-recipient-a" },
      { chatId: "chat-recipient-b" },
    ];
    const hookResultPromise = controller.handleRequestUserInputHook({
      hook_event_name: "PreToolUse",
      session_id: "codex-session-recipient-input",
      turn_id: "turn-recipient-input",
      cwd: directory,
      model: "gpt-5",
      tool_name: "request_user_input",
      tool_input: {
        questions: [{
          header: "范围",
          id: "scope",
          question: "选择范围",
          options: [
            { label: "代码", description: "源代码" },
            { label: "文档", description: "项目文档" },
          ],
          multiple: true,
          custom: false,
        }],
      },
    });
    while (feishu.cards.length < 2) {
      await new Promise<void>((resolve) => setImmediate(resolve));
    }
    const cardA = feishu.cards.find((item) => item.chatId === "chat-recipient-a");
    const cardB = feishu.cards.find((item) => item.chatId === "chat-recipient-b");
    assert.ok(cardA);
    assert.ok(cardB);
    const codeAction = findCardAction(cardA.card, "input_toggle", "代码");
    const docsAction = findCardAction(cardB.card, "input_toggle", "文档");
    const submitA = findCardAction(cardA.card, "input_submit");
    assert.ok(codeAction);
    assert.ok(docsAction);
    assert.ok(submitA);

    await controller.handleCardAction({
      operator: { open_id: "owner" },
      action: { value: JSON.stringify(codeAction) },
    });
    await controller.handleCardAction({
      operator: { open_id: "owner" },
      action: { value: JSON.stringify(docsAction) },
    });
    const latestA = feishu.patchedCards
      .filter((item) => item.messageId === cardA.messageId)
      .at(-1)?.card;
    const latestB = feishu.patchedCards
      .filter((item) => item.messageId === cardB.messageId)
      .at(-1)?.card;
    assert.match(JSON.stringify(latestA), /✓ 代码/);
    assert.doesNotMatch(JSON.stringify(latestA), /✓ 文档/);
    assert.match(JSON.stringify(latestB), /✓ 文档/);
    assert.doesNotMatch(JSON.stringify(latestB), /✓ 代码/);

    await controller.handleCardAction({
      operator: { open_id: "owner" },
      action: { value: JSON.stringify(submitA) },
    });
    const hookResult = await hookResultPromise;
    assert.match(JSON.stringify(hookResult), /代码/);
    assert.doesNotMatch(JSON.stringify(hookResult), /文档/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a failed final answer rolls every recorded question back to interactive", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-input-rollback-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner({
      openId: "owner",
      chatId: "chat-owner",
      chatType: "p2p",
      boundAt: new Date().toISOString(),
    }, code);
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );
    const internal = controller as unknown as {
      inputs: {
        answer: () => Promise<boolean>;
        get: (requestId: string) =>
          | { answers: Record<string, string[]> }
          | undefined;
      };
    };
    internal.inputs.answer = async () => false;
    const hookResultPromise = controller.handleRequestUserInputHook({
      hook_event_name: "PreToolUse",
      session_id: "codex-session-input-rollback",
      turn_id: "turn-input-rollback",
      cwd: directory,
      model: "gpt-5",
      tool_name: "request_user_input",
      tool_input: {
        questions: [
          {
            header: "方式",
            id: "mode",
            question: "选择方式",
            options: [{ label: "检查", description: "只检查" }],
            custom: false,
          },
          {
            header: "范围",
            id: "scope",
            question: "选择范围",
            options: [{ label: "全部", description: "全部处理" }],
            custom: false,
          },
        ],
      },
    });
    while (feishu.cards.length < 2) {
      await new Promise<void>((resolve) => setImmediate(resolve));
    }
    const firstAction = findCardAction(feishu.cards[0]!.card, "input_answer", "检查");
    const secondAction = findCardAction(feishu.cards[1]!.card, "input_answer", "全部");
    const localAction = findCardAction(feishu.cards[0]!.card, "input_local");
    assert.ok(firstAction);
    assert.ok(secondAction);
    assert.ok(localAction);
    await controller.handleCardAction({
      operator: { open_id: "owner" },
      action: { value: JSON.stringify(firstAction) },
    });
    const failed = await controller.handleCardAction({
      operator: { open_id: "owner" },
      action: { value: JSON.stringify(secondAction) },
    });
    assert.equal(failed.toast.type, "warning");
    const waiter = internal.inputs.get(String(firstAction.requestId));
    assert.deepEqual(waiter?.answers, {});
    for (const card of feishu.cards) {
      const patched = feishu.patchedCards
        .filter((item) => item.messageId === card.messageId)
        .at(-1)?.card;
      assert.ok(findCardAction(patched ?? {}, "input_answer"));
      assert.doesNotMatch(JSON.stringify(patched), /已记录/);
    }

    await controller.handleCardAction({
      operator: { open_id: "owner" },
      action: { value: JSON.stringify(localAction) },
    });
    assert.deepEqual(await hookResultPromise, {});
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("Claude AskUserQuestion answers are returned as updatedInput", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-claude-input-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner(
      {
        openId: "owner",
        chatId: "chat-owner",
        chatType: "p2p",
        boundAt: new Date().toISOString(),
      },
      code,
    );
    const sessionId = "b7da810f-78d4-470c-a836-f1aa6a9bb442";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "running",
      source: "startup",
      runtime: "claudecode",
      clientProcessId: process.pid,
    });
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );

    const originalInput = {
      questions: [
        {
          header: "发布方式",
          question: "选择发布方式",
          options: [
            { label: "仅构建", description: "只生成文件" },
            { label: "构建并发布", description: "生成并发布" },
          ],
          multiSelect: true,
        },
      ],
    };
    const hookResultPromise = controller.handleRequestUserInputHook({
      hook_event_name: "PreToolUse",
      session_id: sessionId,
      turn_id: "claude-turn-input-1",
      cwd: directory,
      model: "claude-code",
      runtime: "claudecode",
      tool_name: "request_user_input",
      tool_input: {
        questions: [
          {
            header: "发布方式",
            id: "claude_question_1",
            question: "选择发布方式",
            options: [
              { label: "仅构建", description: "只生成文件" },
              { label: "构建并发布", description: "生成并发布" },
            ],
            multiple: true,
            custom: true,
          },
        ],
        claudeCodeOriginalInput: originalInput,
        claudeCodeQuestionTextById: {
          claude_question_1: "选择发布方式",
        },
      },
    });
    while (feishu.cards.length === 0) {
      await new Promise<void>((resolve) => setImmediate(resolve));
    }
    const questionCardId = feishu.cards[0]!.messageId;
    await controller.handleFeishuMessage(
      messageEvent("claude-input-answer", "owner", "1,2", "p2p", questionCardId),
    );
    const hookResult = await hookResultPromise as {
      hookSpecificOutput?: {
        permissionDecision?: string;
        updatedInput?: Record<string, unknown>;
      };
    };
    assert.equal(hookResult.hookSpecificOutput?.permissionDecision, "allow");
    assert.deepEqual(hookResult.hookSpecificOutput?.updatedInput, {
      ...originalInput,
      answers: { "选择发布方式": "仅构建, 构建并发布" },
      annotations: {},
    });
    assert.equal(feishu.replies.at(-1)?.text, "Claude Code 已接收。");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("Claude PreToolUse resolves the matching local approval and updates its Feishu card", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-claude-local-approval-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner(
      {
        openId: "owner",
        chatId: "chat-owner",
        chatType: "p2p",
        boundAt: new Date().toISOString(),
      },
      code,
    );
    const sessionId = "claude-session-local-approval";
    const toolUseId = "claude-tool-local-approval";
    const turnId = `claudecode-${sessionId}-${toolUseId}`;
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );

    const approvalPromise = controller.handlePermissionHook({
      hook_event_name: "PermissionRequest",
      session_id: sessionId,
      turn_id: turnId,
      cwd: directory,
      model: "claude-code",
      permission_mode: "default",
      tool_name: "Bash",
      tool_input: { command: "npm test" },
      tool_use_id: toolUseId,
      transcript_path: null,
      runtime: "claudecode",
    });
    while (feishu.cards.length === 0) {
      await new Promise<void>((resolve) => setImmediate(resolve));
    }
    const approval = store.listApprovals().find(
      (item) => item.sessionId === sessionId,
    );
    assert.ok(approval);
    assert.equal(approval.toolUseId, toolUseId);
    assert.match(JSON.stringify(feishu.cards[0]?.card), /批准一次/);

    await controller.handleActivityHook({
      hook_event_name: "PreToolUse",
      session_id: sessionId,
      turn_id: `claudecode-${sessionId}-another-tool`,
      cwd: directory,
      runtime: "claudecode",
      tool_name: "Bash",
      tool_use_id: "another-tool",
    });
    assert.equal(store.getApproval(approval.requestId)?.status, "pending");
    assert.equal(feishu.patchedCards.length, 0);

    await controller.handleActivityHook({
      hook_event_name: "PreToolUse",
      session_id: sessionId,
      turn_id: turnId,
      cwd: directory,
      runtime: "claudecode",
      tool_name: "Bash",
      tool_use_id: toolUseId,
    });

    const hookResult = await approvalPromise;
    assert.match(JSON.stringify(hookResult), /"behavior":"allow"/);
    assert.equal(store.getApproval(approval.requestId)?.status, "resolved");
    assert.equal(store.getApproval(approval.requestId)?.resolution, "allow");
    for (
      let attempt = 0;
      attempt < 30 && feishu.patchedCards.length === 0;
      attempt += 1
    ) {
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
    const patchedCard = feishu.patchedCards.find(
      (item) => item.messageId === feishu.cards[0]?.messageId,
    )?.card;
    assert.ok(patchedCard);
    assert.match(JSON.stringify(patchedCard), /审批已处理/);
    assert.match(JSON.stringify(patchedCard), /已批准/);
    assert.doesNotMatch(JSON.stringify(patchedCard), /批准一次/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("Claude SessionEnd releases pending hooks without reviving the ended session", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-claude-end-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner(
      {
        openId: "owner",
        chatId: "chat-owner",
        chatType: "p2p",
        boundAt: new Date().toISOString(),
      },
      code,
    );
    const sessionId = "claude-session-ending-with-hooks";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "running",
      source: "startup",
      runtime: "claudecode",
      clientProcessId: process.pid,
    });
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );

    const approvalPromise = controller.handlePermissionHook({
      hook_event_name: "PermissionRequest",
      session_id: sessionId,
      turn_id: "claude-approval-ending",
      cwd: directory,
      model: "claude-code",
      permission_mode: "default",
      tool_name: "Bash",
      tool_input: { command: "npm test" },
      transcript_path: null,
      runtime: "claudecode",
    });
    const inputPromise = controller.handleRequestUserInputHook({
      hook_event_name: "PreToolUse",
      session_id: sessionId,
      turn_id: "claude-input-ending",
      cwd: directory,
      model: "claude-code",
      runtime: "claudecode",
      tool_name: "request_user_input",
      tool_input: {
        questions: [{
          header: "确认",
          id: "confirm",
          question: "继续吗",
          options: [{ label: "继续", description: "继续" }],
        }],
      },
    });
    while (
      controller.health().pendingApprovals !== 1 ||
      controller.health().pendingInputs !== 1
    ) {
      await new Promise<void>((resolve) => setImmediate(resolve));
    }

    await controller.handleSessionEndHook({
      hook_event_name: "SessionEnd",
      session_id: sessionId,
      cwd: directory,
      reason: "prompt_input_exit",
      transcript_path: null,
      runtime: "claudecode",
    });
    assert.deepEqual(await approvalPromise, {});
    assert.deepEqual(await inputPromise, {});
    assert.equal(controller.health().pendingApprovals, 0);
    assert.equal(controller.health().pendingInputs, 0);
    assert.equal(store.getSession(sessionId)?.status, "ended");
    assert.equal(
      store.listApprovals().find((item) => item.sessionId === sessionId)?.resolution,
      "local",
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("activity hooks reuse one progress card and complete it on Stop", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-activity-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    await store.updateSettings({ notifyActivity: true });
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner(
      {
        openId: "owner",
        chatId: "chat-owner",
        chatType: "p2p",
        boundAt: new Date().toISOString(),
      },
      code,
    );
    const sessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "waiting",
      source: "startup",
      clientProcessId: process.pid,
    });
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );

    await controller.handleActivityHook({
      hook_event_name: "UserPromptSubmit",
      session_id: sessionId,
      turn_id: "turn-activity-1",
      cwd: directory,
    });
    for (let attempt = 0; attempt < 30 && feishu.cards.length === 0; attempt += 1) {
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
    assert.equal(feishu.cards.length, 1);

    await controller.handleActivityHook({
      hook_event_name: "PreToolUse",
      session_id: sessionId,
      turn_id: "turn-activity-1",
      cwd: directory,
      tool_name: "shell_command",
      tool_preview: "npm test",
    });
    await controller.handleStopHook({
      hook_event_name: "Stop",
      session_id: sessionId,
      turn_id: "turn-activity-1",
      cwd: directory,
      model: "gpt-5",
      permission_mode: "default",
      last_assistant_message: "测试完成。",
      stop_hook_active: true,
      transcript_path: null,
    });
    assert.equal(feishu.cards.length, 2);
    assert.ok(feishu.patchedCards.length >= 1);
    assert.match(JSON.stringify(feishu.patchedCards.at(-1)?.card), /本轮处理完成/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("PC prompts can be synchronized to the managed session group without remote echo", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-prompt-sync-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    await store.updateSettings({ notifyUserPrompts: true });
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner({
      openId: "owner",
      chatId: "chat-owner",
      chatType: "p2p",
      boundAt: new Date().toISOString(),
    }, code);
    const sessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    const terminalId = "terminal-prompt-sync";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "waiting",
      source: "startup",
      managedTerminalId: terminalId,
      managedByAssistant: true,
    });
    const feishu = new FakeFeishu();
    const terminals = new FakeManagedTerminals(terminalId, directory, sessionId);
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      terminals as unknown as ManagedTerminalRouter,
      undefined,
      controllerConfig(directory),
    );
    await controller.handleSessionStartHook({
      hook_event_name: "SessionStart",
      session_id: sessionId,
      cwd: directory,
      model: "gpt-5",
      permission_mode: "default",
      source: "startup",
      transcript_path: null,
      managed_terminal_id: terminalId,
    });
    for (let attempt = 0; attempt < 20 && !store.getSession(sessionId)?.feishuChatId; attempt += 1) {
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
    const chatId = store.getSession(sessionId)?.feishuChatId;
    assert.ok(chatId);
    await controller.handleFeishuMessage(
      groupMessageEvent("remote-prompt-2", "owner", chatId, "飞书输入"),
    );
    await controller.handleActivityHook({
      hook_event_name: "UserPromptSubmit",
      session_id: sessionId,
      turn_id: "turn-remote",
      cwd: directory,
      prompt: "飞书输入",
    });
    assert.equal(feishu.cards.filter((item) => JSON.stringify(item.card).includes("电脑端已提交消息")).length, 0);

    await controller.handleActivityHook({
      hook_event_name: "UserPromptSubmit",
      session_id: sessionId,
      turn_id: "turn-local",
      cwd: directory,
      prompt: "本机输入",
    });
    for (let attempt = 0; attempt < 20 &&
      feishu.cards.filter((item) => JSON.stringify(item.card).includes("电脑端已提交消息")).length === 0;
      attempt += 1) {
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
    assert.equal(feishu.cards.filter((item) => JSON.stringify(item.card).includes("电脑端已提交消息")).length, 1);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a transcript task error is notified even when Codex skips the Stop hook", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-transcript-error-"));
  const transcriptPath = path.join(directory, "rollout.jsonl");
  let store: BridgeStore | undefined;
  let controller: BridgeController | undefined;
  try {
    store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner({
      openId: "owner",
      chatId: "chat-owner",
      chatType: "p2p",
      boundAt: new Date().toISOString(),
    }, code);
    await writeFile(transcriptPath, "", "utf8");
    const sessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    const terminalId = "terminal-transcript-error";
    const feishu = new FakeFeishu();
    const terminals = new FakeManagedTerminals(terminalId, directory, sessionId);
    controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      terminals as unknown as ManagedTerminalRouter,
      undefined,
      controllerConfig(directory),
    );
    await controller.handleSessionStartHook({
      hook_event_name: "SessionStart",
      session_id: sessionId,
      cwd: directory,
      model: "gpt-5",
      permission_mode: "default",
      source: "startup",
      transcript_path: transcriptPath,
      managed_terminal_id: terminalId,
    });

    await appendFile(transcriptPath, `${JSON.stringify({
      type: "event_msg",
      payload: {
        type: "task_complete",
        turn_id: "turn-transcript-error",
        last_agent_message: null,
        error: {
          message: "We're currently experiencing high demand, which may cause temporary errors.",
          codex_error_info: "internal_server_error",
        },
      },
    })}\n`, "utf8");

    for (let attempt = 0; attempt < 100 && feishu.cards.length === 0; attempt += 1) {
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
    assert.equal(feishu.cards.length, 1);
    assert.match(JSON.stringify(feishu.cards[0]?.card), /Codex 运行错误/);
    assert.match(JSON.stringify(feishu.cards[0]?.card), /high demand/);
    assert.equal(store.getSession(sessionId)?.status, "error");
    assert.equal(
      store.getSession(sessionId)?.lastNotificationTurnId,
      "turn-transcript-error",
    );
  } finally {
    await controller?.close();
    await store?.close();
    await rm(directory, { recursive: true, force: true });
  }
});

test("a pending turn notification is recovered without creating a duplicate card", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-notification-recovery-"));
  const sessionId = "019faef0-d0bb-7703-af82-17ee9b45397d";
  const turnId = "turn-notification-recovery";
  const feishu = new FakeFeishu();
  let firstController: BridgeController | undefined;
  let recoveredController: BridgeController | undefined;
  let firstStore: BridgeStore | undefined;
  let recoveredStore: BridgeStore | undefined;
  try {
    firstStore = new BridgeStore(directory);
    await firstStore.init();
    const code = firstStore.getPairingCode();
    assert.ok(code);
    await firstStore.bindOwner({
      openId: "owner",
      chatId: "chat-owner",
      chatType: "p2p",
      boundAt: new Date().toISOString(),
    }, code);
    await firstStore.upsertSession({
      sessionId,
      cwd: directory,
      status: "running",
      runtime: "codex",
    });
    firstController = new BridgeController(
      firstStore,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );
    firstStore.completeTurnNotification = async () => {
      throw new Error("simulated crash before notification completion persisted");
    };

    await assert.rejects(
      firstController.handleStopHook({
        hook_event_name: "Stop",
        session_id: sessionId,
        turn_id: turnId,
        cwd: directory,
        model: "gpt-5",
        last_assistant_message: "处理完成。",
        stop_hook_active: true,
        transcript_path: null,
        runtime: "codex",
      }),
      /simulated crash/u,
    );
    assert.equal(feishu.cards.length, 1);
    assert.equal(firstStore.getSession(sessionId)?.lastNotificationStatus, "pending");

    await firstController.close();
    firstController = undefined;
    await firstStore.close();
    firstStore = undefined;

    recoveredStore = new BridgeStore(directory);
    await recoveredStore.init();
    recoveredController = new BridgeController(
      recoveredStore,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );
    await recoveredController.initialize();

    assert.equal(feishu.cards.length, 1);
    assert.equal(recoveredStore.getSession(sessionId)?.lastNotificationStatus, "sent");
    assert.equal(feishu.cardIdempotencyAttempts.length, 2);
    assert.equal(
      feishu.cardIdempotencyAttempts[0],
      feishu.cardIdempotencyAttempts[1],
    );
  } finally {
    await firstController?.close();
    await recoveredController?.close();
    await firstStore?.close();
    await recoveredStore?.close();
    await rm(directory, { recursive: true, force: true });
  }
});

test("a managed 502 gateway error is notified and retried when enabled", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-retry-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    await store.updateSettings({ autoRetryErrors: true });
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner({
      openId: "owner",
      chatId: "chat-owner",
      chatType: "p2p",
      boundAt: new Date().toISOString(),
    }, code);
    const sessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    const terminalId = "terminal-retry-1";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "running",
      source: "startup",
      managedTerminalId: terminalId,
    });
    const feishu = new FakeFeishu();
    const terminals = new FakeManagedTerminals(terminalId, directory, sessionId);
    const transcriptPath = path.join(directory, "rollout.jsonl");
    await writeFile(transcriptPath, JSON.stringify({
      type: "event_msg",
      payload: {
        type: "task_complete",
        turn_id: "turn-retry-1",
        last_agent_message: null,
        error: {
          message: "API Error: 502 Bad Gateway",
          codex_error_info: "bad_gateway_502",
        },
      },
    }));
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      terminals as unknown as ManagedTerminalRouter,
      undefined,
      controllerConfig(directory),
    );
    await controller.handleStopHook({
      hook_event_name: "Stop",
      session_id: sessionId,
      turn_id: "turn-retry-1",
      cwd: directory,
      model: "gpt-5",
      permission_mode: "default",
      last_assistant_message: null,
      stop_hook_active: true,
      transcript_path: transcriptPath,
    });
    assert.equal(feishu.cards.length, 1);
    assert.match(JSON.stringify(feishu.cards[0]?.card), /Codex 运行错误/);
    for (let attempt = 0; attempt < 20 && terminals.sends.length === 0; attempt += 1) {
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
    assert.equal(terminals.sends.length, 1);
    assert.match(terminals.sends[0]!.prompt, /重试上一项任务/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a pending automatic retry can be stopped from its Feishu card", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-stop-retry-"));
  let controller: BridgeController | undefined;
  let store: BridgeStore | undefined;
  try {
    store = new BridgeStore(directory);
    await store.init();
    await store.updateSettings({
      autoRetryErrors: true,
      retryMaxAttempts: 3,
      retryJitterSeconds: 0,
    });
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner({
      openId: "owner",
      chatId: "chat-owner",
      chatType: "p2p",
      boundAt: new Date().toISOString(),
    }, code);
    const sessionId = "codex-session-stop-retry";
    const terminalId = "terminal-stop-retry";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "running",
      source: "startup",
      runtime: "codex",
      managedTerminalId: terminalId,
    });
    const feishu = new FakeFeishu();
    const terminals = new FakeManagedTerminals(terminalId, directory, sessionId);
    controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      terminals as unknown as ManagedTerminalRouter,
      undefined,
      {
        ...controllerConfig(directory),
        retryBaseDelayMs: 200,
      },
    );

    await controller.handleStopHook({
      hook_event_name: "Stop",
      session_id: sessionId,
      turn_id: "turn-stop-retry",
      cwd: directory,
      model: "gpt-5",
      permission_mode: "default",
      last_assistant_message: "API Error: 502 Bad Gateway",
      stop_hook_active: true,
      transcript_path: null,
      runtime: "codex",
    });

    assert.equal(feishu.cards.length, 1);
    const stopAction = findCardAction(feishu.cards[0]!.card, "retry_stop");
    assert.ok(stopAction);
    const stopped = await controller.handleCardAction({
      operator: { open_id: "owner" },
      action: { value: JSON.stringify(stopAction) },
    });
    assert.equal(stopped.toast.type, "success");
    assert.match(stopped.toast.content, /停止自动重试/);
    assert.doesNotMatch(JSON.stringify(stopped.card), /"action":"retry_stop"/);

    await new Promise((resolve) => setTimeout(resolve, 250));
    assert.equal(terminals.sends.length, 0);
    const patched = feishu.patchedCards
      .filter((item) => item.messageId === feishu.cards[0]!.messageId)
      .at(-1)?.card;
    assert.match(JSON.stringify(patched), /已停止自动重试/);
    assert.doesNotMatch(JSON.stringify(patched), /"action":"retry_stop"/);
  } finally {
    await controller?.close();
    await store?.close();
    await rm(directory, { recursive: true, force: true });
  }
});

test("stopping a running retry prevents every later automatic attempt", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-stop-running-retry-"));
  let controller: BridgeController | undefined;
  let store: BridgeStore | undefined;
  try {
    store = new BridgeStore(directory);
    await store.init();
    await store.updateSettings({
      autoRetryErrors: true,
      retryMaxAttempts: 3,
      retryJitterSeconds: 0,
    });
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner({
      openId: "owner",
      chatId: "chat-owner",
      chatType: "p2p",
      boundAt: new Date().toISOString(),
    }, code);
    const sessionId = "codex-session-stop-running-retry";
    const terminalId = "terminal-stop-running-retry";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "running",
      source: "startup",
      runtime: "codex",
      managedTerminalId: terminalId,
    });
    const feishu = new FakeFeishu();
    const terminals = new FakeManagedTerminals(terminalId, directory, sessionId);
    controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      terminals as unknown as ManagedTerminalRouter,
      undefined,
      {
        ...controllerConfig(directory),
        retryBaseDelayMs: 10,
      },
    );

    await controller.handleStopHook({
      hook_event_name: "Stop",
      session_id: sessionId,
      turn_id: "turn-running-retry-1",
      cwd: directory,
      model: "gpt-5",
      permission_mode: "default",
      last_assistant_message: "API Error: 502 Bad Gateway",
      stop_hook_active: true,
      transcript_path: null,
      runtime: "codex",
    });
    for (let attempt = 0; attempt < 20 && terminals.sends.length === 0; attempt += 1) {
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
    assert.equal(terminals.sends.length, 1);
    const stopAction = findCardAction(feishu.cards[0]!.card, "retry_stop");
    assert.ok(stopAction);
    const stopped = await controller.handleCardAction({
      operator: { open_id: "owner" },
      action: { value: JSON.stringify(stopAction) },
    });
    assert.equal(stopped.toast.type, "success");
    assert.match(stopped.toast.content, /已经发送.*停止后续自动重试/);

    await controller.handleStopHook({
      hook_event_name: "Stop",
      session_id: sessionId,
      turn_id: "turn-running-retry-2",
      cwd: directory,
      model: "gpt-5",
      permission_mode: "default",
      last_assistant_message: "API Error: 502 Bad Gateway",
      stop_hook_active: true,
      transcript_path: null,
      runtime: "codex",
    });
    await new Promise((resolve) => setTimeout(resolve, 50));
    assert.equal(terminals.sends.length, 1);
    assert.doesNotMatch(
      JSON.stringify(feishu.cards.at(-1)?.card),
      /"action":"retry_stop"/,
    );
  } finally {
    await controller?.close();
    await store?.close();
    await rm(directory, { recursive: true, force: true });
  }
});

for (const runtime of ["codex", "claudecode"] as const) {
  test(`${runtime} retry attempts reset after a successful turn`, async () => {
    const directory = await mkdtemp(path.join(os.tmpdir(), `ai-cli-feishu-${runtime}-reset-`));
    let controller: BridgeController | undefined;
    let store: BridgeStore | undefined;
    try {
      store = new BridgeStore(directory);
      await store.init();
      await store.updateSettings({
        autoRetryErrors: true,
        retryMaxAttempts: 1,
        retryJitterSeconds: 0,
      });
      const code = store.getPairingCode();
      assert.ok(code);
      await store.bindOwner({
        openId: "owner",
        chatId: "chat-owner",
        chatType: "p2p",
        boundAt: new Date().toISOString(),
      }, code);
      const sessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
      const terminalId = `terminal-${runtime}-retry-reset`;
      await store.upsertSession({
        sessionId,
        cwd: directory,
        status: "running",
        source: "startup",
        runtime,
        managedTerminalId: terminalId,
      });
      const feishu = new FakeFeishu();
      const terminals = new FakeManagedTerminals(terminalId, directory, sessionId);
      controller = new BridgeController(
        store,
        feishu as unknown as FeishuGateway,
        new FakeCodex() as unknown as CodexRunner,
        terminals as unknown as ManagedTerminalRouter,
        undefined,
        controllerConfig(directory),
      );
      const temporaryError =
        "We're currently experiencing high demand, which may cause temporary errors.";

      await controller.handleStopHook({
        hook_event_name: "Stop",
        session_id: sessionId,
        turn_id: "turn-retry-batch-1",
        cwd: directory,
        model: "test-model",
        permission_mode: "default",
        last_assistant_message: temporaryError,
        stop_hook_active: true,
        transcript_path: null,
        runtime,
      });
      for (let attempt = 0; attempt < 20 && terminals.sends.length < 1; attempt += 1) {
        await new Promise((resolve) => setTimeout(resolve, 10));
      }
      assert.equal(terminals.sends.length, 1);

      await controller.handleStopHook({
        hook_event_name: "Stop",
        session_id: sessionId,
        turn_id: "turn-retry-batch-1",
        cwd: directory,
        model: "test-model",
        permission_mode: "default",
        last_assistant_message: "本轮重试已成功完成。",
        stop_hook_active: true,
        transcript_path: null,
        runtime,
      });
      await controller.handleStopHook({
        hook_event_name: "Stop",
        session_id: sessionId,
        turn_id: "turn-retry-batch-2",
        cwd: directory,
        model: "test-model",
        permission_mode: "default",
        last_assistant_message: temporaryError,
        stop_hook_active: true,
        transcript_path: null,
        runtime,
      });
      for (let attempt = 0; attempt < 20 && terminals.sends.length < 2; attempt += 1) {
        await new Promise((resolve) => setTimeout(resolve, 10));
      }

      assert.equal(terminals.sends.length, 2);
      const retryCards = feishu.cards.filter((entry) =>
        JSON.stringify(entry.card).includes("第 1/1 次")
      );
      assert.equal(retryCards.length, 2);
    } finally {
      await controller?.close();
      await store?.close();
      await rm(directory, { recursive: true, force: true });
    }
  });
}

test("a structured permanent Codex error is notified without automatic retry", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-permanent-error-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    await store.updateSettings({ autoRetryErrors: true });
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner({
      openId: "owner",
      chatId: "chat-owner",
      chatType: "p2p",
      boundAt: new Date().toISOString(),
    }, code);
    const sessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    const terminalId = "terminal-permanent-error";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "running",
      source: "startup",
      managedTerminalId: terminalId,
    });
    const transcriptPath = path.join(directory, "rollout.jsonl");
    await writeFile(transcriptPath, JSON.stringify({
      type: "event_msg",
      payload: {
        type: "task_complete",
        turn_id: "turn-permanent-error",
        last_agent_message: null,
        error: {
          message: "Authentication failed. Please sign in again.",
          codex_error_info: "authentication_error",
        },
      },
    }));
    const feishu = new FakeFeishu();
    const terminals = new FakeManagedTerminals(terminalId, directory, sessionId);
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      terminals as unknown as ManagedTerminalRouter,
      undefined,
      controllerConfig(directory),
    );

    await controller.handleStopHook({
      hook_event_name: "Stop",
      session_id: sessionId,
      turn_id: "turn-permanent-error",
      cwd: directory,
      model: "gpt-5",
      permission_mode: "default",
      last_assistant_message: null,
      stop_hook_active: true,
      transcript_path: transcriptPath,
    });

    assert.equal(feishu.cards.length, 1);
    assert.match(JSON.stringify(feishu.cards[0]?.card), /Authentication failed/);
    assert.equal(store.getSession(sessionId)?.status, "error");
    await new Promise((resolve) => setTimeout(resolve, 30));
    assert.equal(terminals.sends.length, 0);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("normal explanatory replies containing status codes and error words are not error cards", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-error-detection-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner({
      openId: "owner",
      chatId: "chat-owner",
      chatType: "p2p",
      boundAt: new Date().toISOString(),
    }, code);
    const sessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "waiting",
      source: "startup",
      clientProcessId: process.pid,
    });
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );
    await controller.handleStopHook({
      hook_event_name: "Stop",
      session_id: sessionId,
      turn_id: "turn-normal-explanation",
      cwd: directory,
      model: "gpt-5",
      permission_mode: "default",
      last_assistant_message: "已完成修正。说明：之前的 400 错误和失败状态是桥接器误报。",
      stop_hook_active: true,
      transcript_path: null,
    });
    assert.equal(feishu.cards.length, 1);
    assert.doesNotMatch(JSON.stringify(feishu.cards[0]?.card), /Codex 运行错误/);
    assert.equal(store.getSession(sessionId)?.status, "waiting");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("compact service error lines still produce an error card", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-error-line-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner({
      openId: "owner",
      chatId: "chat-owner",
      chatType: "p2p",
      boundAt: new Date().toISOString(),
    }, code);
    const sessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "waiting",
      source: "startup",
      clientProcessId: process.pid,
    });
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );
    await controller.handleStopHook({
      hook_event_name: "Stop",
      session_id: sessionId,
      turn_id: "turn-compact-error",
      cwd: directory,
      model: "gpt-5",
      permission_mode: "default",
      last_assistant_message: "Error 503: service unavailable, request failed.",
      stop_hook_active: true,
      transcript_path: null,
    });
    assert.equal(feishu.cards.length, 1);
    assert.match(JSON.stringify(feishu.cards[0]?.card), /Codex 运行错误/);
    assert.equal(store.getSession(sessionId)?.status, "error");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("an explicit file request returns only project files and hides the protocol line", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-return-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner(
      {
        openId: "owner",
        chatId: "chat-owner",
        chatType: "p2p",
        boundAt: new Date().toISOString(),
      },
      code,
    );
    const sessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    const terminalId = "terminal-file-return";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "waiting",
      source: "startup",
      managedTerminalId: terminalId,
    });
    const feishu = new FakeFeishu();
    const terminals = new FakeManagedTerminals(terminalId, directory, sessionId);
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      terminals as unknown as ManagedTerminalRouter,
      undefined,
      controllerConfig(directory),
    );

    await controller.handleFeishuMessage(
      messageEvent("file-request", "owner", "发文件 生成一份报告"),
    );
    assert.match(terminals.sends[0]?.prompt ?? "", /BRIDGE_SEND_FILE/);
    const report = path.join(directory, "report.txt");
    await writeFile(report, "done", "utf8");
    await controller.handleStopHook({
      hook_event_name: "Stop",
      session_id: sessionId,
      turn_id: "turn-file-1",
      cwd: directory,
      model: "gpt-5",
      permission_mode: "default",
      last_assistant_message: `报告已生成。\nBRIDGE_SEND_FILE: ${report}`,
      stop_hook_active: true,
      transcript_path: null,
    });
    for (let attempt = 0; attempt < 20 && feishu.localFiles.length === 0; attempt += 1) {
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
    assert.deepEqual(feishu.localFiles, [
      { chatId: "chat-owner", filePath: await realpath(report) },
    ]);
    assert.doesNotMatch(JSON.stringify(feishu.cards.at(-1)?.card), /BRIDGE_SEND_FILE/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a Feishu image is staged and attached to the next routed prompt", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-upload-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner(
      {
        openId: "owner",
        chatId: "chat-owner",
        chatType: "p2p",
        boundAt: new Date().toISOString(),
      },
      code,
    );
    const sessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    const terminalId = "terminal-image-upload";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "waiting",
      source: "startup",
      managedTerminalId: terminalId,
    });
    const feishu = new FakeFeishu();
    const terminals = new FakeManagedTerminals(terminalId, directory, sessionId);
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      terminals as unknown as ManagedTerminalRouter,
      undefined,
      controllerConfig(directory),
    );

    await controller.handleFeishuMessage({
      sender: { sender_id: { open_id: "owner" } },
      message: {
        message_id: "image-message",
        chat_id: "chat-owner",
        chat_type: "p2p",
        message_type: "image",
        content: JSON.stringify({ image_key: "image_key_1" }),
      },
    });
    assert.match(feishu.replies.at(-1)?.text ?? "", /已安全保存 1 个附件/);
    await controller.handleFeishuMessage(
      messageEvent("image-instruction", "owner", "分析这张图片"),
    );
    assert.match(terminals.sends[0]?.prompt ?? "", /uploads[\\/]/);
    assert.match(terminals.sends[0]?.prompt ?? "", /用户要求：分析这张图片/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("an explicit managed terminal id wins when two windows share a cwd", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-claim-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const router = new ManagedTerminalRouter();
    router.register({ terminalId: "terminal666", cwd: directory, ready: false });
    router.register({ terminalId: "terminal777", cwd: directory, ready: false });
    const controller = new BridgeController(
      store,
      new FakeFeishu() as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      router,
      undefined,
      controllerConfig(directory),
    );

    const startingSessions = controller.health().sessions as Array<{
      status: string;
      openedAt: string;
      managedTerminalReady: boolean;
    }>;
    assert.equal(startingSessions.length, 2);
    assert.ok(startingSessions.every((item) => item.status === "starting"));
    assert.ok(startingSessions.every((item) => item.openedAt.length > 0));
    assert.ok(startingSessions.every((item) => item.managedTerminalReady === false));

    await controller.handleSessionStartHook({
      hook_event_name: "SessionStart",
      session_id: "019faef0-d0bb-7703-af82-17ee9b45397b",
      cwd: directory,
      model: "gpt-5",
      permission_mode: "default",
      source: "startup",
      transcript_path: null,
      managed_terminal_id: "terminal777",
    });
    assert.equal(
      store.getSession("019faef0-d0bb-7703-af82-17ee9b45397b")?.managedTerminalId,
      "terminal777",
    );
    assert.equal(
      store.getSession("019faef0-d0bb-7703-af82-17ee9b45397b")?.managedByAssistant,
      true,
    );
    const activeHealth = controller.health() as {
      historySessions: Array<{ sessionId: string }>;
    };
    assert.equal(activeHealth.historySessions.length, 0);

    await controller.handleManagedTerminalUnregistration({ terminalId: "terminal777" });
    const closedHealth = controller.health() as {
      historySessions: Array<{
        sessionId: string;
        managedByAssistant: boolean;
        status: string;
      }>;
    };
    assert.equal(closedHealth.historySessions.length, 1);
    assert.equal(
      closedHealth.historySessions[0]?.sessionId,
      "019faef0-d0bb-7703-af82-17ee9b45397b",
    );
    assert.equal(closedHealth.historySessions[0]?.managedByAssistant, true);
    assert.equal(closedHealth.historySessions[0]?.status, "ended");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("resuming outside the helper keeps history but clears managed-window metadata", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-external-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const code = store.getPairingCode();
    assert.ok(code);
    await store.bindOwner(
      {
        openId: "owner",
        chatId: "chat-owner",
        chatType: "p2p",
        boundAt: new Date().toISOString(),
      },
      code,
    );
    const sessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "waiting",
      openedAt: "2020-01-01T00:00:00.000Z",
      managedTerminalId: "terminal888",
      managedTerminalElevated: true,
    });
    await store.setSessionFeishuChat(sessionId, {
      chatId: "external-session-chat",
      chatName: "external session",
    });
    await store.upsertSession({ sessionId, cwd: directory, status: "ended" });
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      controllerConfig(directory),
    );

    await controller.handleSessionStartHook({
      hook_event_name: "SessionStart",
      session_id: sessionId,
      cwd: directory,
      model: "gpt-5",
      permission_mode: "default",
      source: "resume",
      transcript_path: null,
    });
    const resumed = store.getSession(sessionId);
    assert.equal(resumed?.managedTerminalId, undefined);
    assert.equal(resumed?.managedTerminalElevated, undefined);
    assert.equal(resumed?.managedByAssistant, false);
    assert.equal(resumed?.historyEligible, true);
    assert.notEqual(resumed?.openedAt, "2020-01-01T00:00:00.000Z");
    await controller.handleFeishuMessage(
      groupMessageEvent(
        "external-session-reply",
        "owner",
        "external-session-chat",
        "继续完成剩余工作",
      ),
    );
    assert.match(
      feishu.replies.at(-1)?.text ?? "",
      /不是由 AI CLI 飞书助手打开.*不能从飞书回复/,
    );
    assert.equal(
      (controller.handleRuntimeLaunchClaim() as { request?: unknown }).request,
      undefined,
    );
    await controller.handleSessionEndHook({
      hook_event_name: "SessionEnd",
      session_id: sessionId,
      cwd: directory,
      reason: "other",
      transcript_path: null,
    });
    const history = (controller.health() as {
      historySessions: Array<{ sessionId: string; managedByAssistant: boolean }>;
    }).historySessions;
    assert.equal(history.some((item) => item.sessionId === sessionId), true);
    assert.equal(
      history.find((item) => item.sessionId === sessionId)?.managedByAssistant,
      false,
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("external sessions follow their real process and untracked records expire quickly", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-external-life-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const stale = await store.upsertSession({
      sessionId: "external-untracked-stale",
      cwd: directory,
      status: "waiting",
      source: "startup",
    });
    stale.lastSeenAt = new Date(Date.now() - 6 * 60 * 1000).toISOString();

    const alive = await store.upsertSession({
      sessionId: "external-process-alive",
      cwd: directory,
      status: "waiting",
      source: "startup",
      clientProcessId: process.pid,
    });
    alive.lastSeenAt = new Date(Date.now() - 24 * 60 * 60 * 1000).toISOString();

    await store.upsertSession({
      sessionId: "external-process-dead",
      cwd: directory,
      status: "waiting",
      source: "startup",
      clientProcessId: 2_147_483_646,
    });

    const controller = new BridgeController(
      store,
      new FakeFeishu() as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      undefined,
      {
        ...controllerConfig(directory),
        sessionActiveMs: 24 * 60 * 60 * 1000,
      },
    );
    const sessions = controller.health().sessions as Array<{
      sessionId: string;
      externalProcessTracked: boolean;
    }>;
    assert.deepEqual(sessions.map((item) => item.sessionId), [alive.sessionId]);
    assert.equal(sessions[0]?.externalProcessTracked, true);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

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

function findCardActions(
  value: unknown,
  action: string,
): Array<Record<string, unknown>> {
  if (!value || typeof value !== "object") {
    return [];
  }
  if (Array.isArray(value)) {
    return value.flatMap((item) => findCardActions(item, action));
  }
  const record = value as Record<string, unknown>;
  return [
    ...(record.action === action ? [record] : []),
    ...Object.values(record).flatMap((child) =>
      findCardActions(child, action)
    ),
  ];
}
