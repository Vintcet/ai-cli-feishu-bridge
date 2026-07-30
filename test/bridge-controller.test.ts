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
import { BridgeStore } from "../src/store.js";

class FakeFeishu {
  readonly replies: Array<{ messageId: string; text: string }> = [];
  readonly cards: Array<{ chatId: string; card: Record<string, unknown>; messageId: string }> = [];
  readonly patchedCards: Array<{ messageId: string; card: Record<string, unknown> }> = [];
  readonly localFiles: Array<{ chatId: string; filePath: string }> = [];
  private counter = 0;

  async replyText(messageId: string, text: string): Promise<string> {
    this.replies.push({ messageId, text });
    return `reply-${++this.counter}`;
  }

  async sendText(_chatId: string, text: string): Promise<string> {
    this.replies.push({ messageId: "new", text });
    return `message-${++this.counter}`;
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

class FakeManagedTerminals {
  readonly sends: Array<{ prompt: string; submitMode: string }> = [];

  constructor(
    private readonly terminalId: string,
    private readonly cwd: string,
    private readonly sessionId: string,
  ) {}

  isManaged(session: SessionRecord): boolean {
    return session.managedTerminalId === this.terminalId;
  }

  isOnline(): boolean {
    return true;
  }

  isReady(): boolean {
    return true;
  }

  listOnline() {
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
      controllerConfig(directory),
    );
    const code = store.getPairingCode();
    assert.ok(code);

    await controller.handleFeishuMessage(
      messageEvent("bind-group", "owner", `绑定 ${code}`, "group"),
    );
    assert.equal(store.listBindings().length, 0);
    assert.match(feishu.replies.at(-1)?.text ?? "", /私聊/);

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

test("an external session queues additional Feishu replies", async () => {
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
      controllerConfig(directory),
    );

    await Promise.all([
      controller.handleFeishuMessage(messageEvent("prompt-1", "owner", "第一条")),
      controller.handleFeishuMessage(messageEvent("prompt-2", "owner", "第二条")),
    ]);
    assert.equal(codex.resumeCount, 1);
    assert.equal(codex.prompts.length, 1);
    assert.ok(feishu.replies.some((item) => /桥接队列第 1 位/.test(item.text)));
    assert.equal(controller.health().queuedPrompts, 1);

    await codex.finish();
    assert.equal(codex.resumeCount, 2);
    assert.equal(codex.prompts.length, 2);
    assert.equal(controller.health().queuedPrompts, 0);
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
    const controller = new BridgeController(
      store,
      new FakeFeishu() as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      terminals as unknown as ManagedTerminalRouter,
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
    assert.equal(controller.health().queuedPrompts, 1);
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

test("activity hooks reuse one progress card and complete it on Stop", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-activity-"));
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
      source: "startup",
      clientProcessId: process.pid,
    });
    const feishu = new FakeFeishu();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      new FakeCodex() as unknown as CodexRunner,
      new ManagedTerminalRouter(),
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
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "waiting",
      source: "startup",
      clientProcessId: process.pid,
    });
    const feishu = new FakeFeishu();
    const codex = new FakeCodex();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      codex as unknown as CodexRunner,
      new ManagedTerminalRouter(),
      controllerConfig(directory),
    );

    await controller.handleFeishuMessage(
      messageEvent("file-request", "owner", "发文件 生成一份报告"),
    );
    assert.match(codex.prompts[0] ?? "", /BRIDGE_SEND_FILE/);
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
    await store.upsertSession({
      sessionId,
      cwd: directory,
      status: "waiting",
      source: "startup",
      clientProcessId: process.pid,
    });
    const feishu = new FakeFeishu();
    const codex = new FakeCodex();
    const controller = new BridgeController(
      store,
      feishu as unknown as FeishuGateway,
      codex as unknown as CodexRunner,
      new ManagedTerminalRouter(),
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
    assert.match(codex.prompts[0] ?? "", /uploads[\\/]/);
    assert.match(codex.prompts[0] ?? "", /用户要求：分析这张图片/);
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
