import assert from "node:assert/strict";
import { appendFile, mkdtemp, rm, stat, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";

import { BridgeController } from "../src/bridge-controller.js";
import type { CodexExitResult, CodexRunner } from "../src/codex-runner.js";
import type { SessionRecord } from "../src/domain.js";
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
  private counter = 0;
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

  async sendCard(chatId: string, card: Record<string, unknown>): Promise<string> {
    const messageId = `card-${++this.counter}`;
    this.cards.push({ chatId, card, messageId });
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
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-controller-"));
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
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-session-group-"));
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

test("dissolves assistant session groups after one inactive week", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-group-cleanup-"));
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
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-auto-resume-"));
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
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-opencode-auto-resume-"));
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
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-opencode-active-"));
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
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-new-runtime-"));
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
      const expectedCwd = path.join(workspaceRoot, item.projectName);
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
    assert.equal(existingClaim.request?.cwd, path.join(workspaceRoot, "主项目"));
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
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-group-retry-"));
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

test("history removal hides only assistant-managed sessions", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-history-hide-"));
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
      sessionId: "external-session",
      cwd: directory,
      status: "ended",
    });

    assert.equal(
      (await controller.handleSessionHistoryHide({})).ok,
      false,
    );
    assert.equal(
      (await controller.handleSessionHistoryHide({ sessionId: "external-session" })).ok,
      false,
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

test("retry settings accept bounded integers and reject invalid values", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-settings-"));
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
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-order-"));
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
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-lock-"));
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
      controller.handleFeishuMessage(messageEvent("prompt-1", "owner", "第一条")),
      controller.handleFeishuMessage(messageEvent("prompt-2", "owner", "第二条")),
    ]);
    assert.equal(codex.resumeCount, 0);
    assert.equal(codex.prompts.length, 0);
    assert.deepEqual(
      feishu.replies.map((item) => item.text),
      [
        "Codex 未接收：外部会话不支持飞书输入。请回到原窗口继续。",
        "Codex 未接收：外部会话不支持飞书输入。请回到原窗口继续。",
      ],
    );
    assert.equal(controller.health().queuedPrompts, 0);
    assert.equal(
      store.getSession("019faef0-d0bb-7703-af82-17ee9b45397b")?.status,
      "waiting",
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a managed session steers by default and queues explicitly", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-steer-"));
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
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-approval-"));
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

test("an approval completed in Feishu is visible as resolved to the desktop", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-approval-sync-"));
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
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("automatic approval allows the hook and resolves the Feishu card", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-auto-approval-"));
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
    assert.equal(feishu.cards.length, 1);
    assert.equal(feishu.patchedCards.length, 1);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("request_user_input can be answered by replying to the Feishu card", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-input-"));
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

test("Claude AskUserQuestion answers are returned as updatedInput", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-claude-input-"));
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

test("Claude SessionEnd releases pending hooks without reviving the ended session", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-claude-end-"));
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
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-activity-"));
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
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-prompt-sync-"));
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
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-transcript-error-"));
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

test("a managed temporary error is notified and retried when enabled", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-retry-"));
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
          message: "We're currently experiencing high demand, which may cause temporary errors.",
          codex_error_info: "internal_server_error",
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

test("a structured permanent Codex error is notified without automatic retry", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-permanent-error-"));
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
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-error-detection-"));
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
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-error-line-"));
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
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-return-"));
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
    assert.deepEqual(feishu.localFiles, [{ chatId: "chat-owner", filePath: report }]);
    assert.doesNotMatch(JSON.stringify(feishu.cards.at(-1)?.card), /BRIDGE_SEND_FILE/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a Feishu image is staged and attached to the next routed prompt", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-upload-"));
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
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-claim-"));
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

test("resuming outside the helper clears stale managed-window metadata", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-external-"));
  try {
    const store = new BridgeStore(directory);
    await store.init();
    const sessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "waiting",
      openedAt: "2020-01-01T00:00:00.000Z",
      managedTerminalId: "terminal888",
      managedTerminalElevated: true,
    });
    await store.upsertSession({ sessionId, cwd: directory, status: "ended" });
    const controller = new BridgeController(
      store,
      new FakeFeishu() as unknown as FeishuGateway,
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
    assert.notEqual(resumed?.openedAt, "2020-01-01T00:00:00.000Z");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("external sessions follow their real process and untracked records expire quickly", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-external-life-"));
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
