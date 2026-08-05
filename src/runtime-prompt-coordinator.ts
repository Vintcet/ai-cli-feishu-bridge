import { randomUUID } from "node:crypto";

import { ApprovalCoordinator } from "./approval-coordinator.js";
import type { RuntimeAdapterRegistry } from "./bridge-protocol/runtime-adapter-registry.js";
import type { CodexExitResult } from "./codex-runner.js";
import { CodexRunner } from "./codex-runner.js";
import {
  runtimeDefinition,
  runtimeDisplayName,
  runtimeReceivedText,
  truncate,
  type SessionRecord,
} from "./domain.js";
import { FileTransferCoordinator } from "./file-transfer-coordinator.js";
import { ManagedTerminalRouter } from "./managed-terminal.js";
import { OpenCodeManager } from "./opencode-manager.js";
import { RuntimeRetryCoordinator } from "./runtime-retry-coordinator.js";
import { ActivityCoordinator } from "./activity-coordinator.js";
import { BridgeStore } from "./store.js";
import { UserInputCoordinator } from "./user-input-coordinator.js";

interface QueuedRemotePrompt {
  prompt: string;
  sourceMessageId: string;
  chatId: string;
  requestFileReturn: boolean;
  retryAttempt?: number;
}

interface PendingRemotePrompt {
  prompt: string;
  createdAt: number;
}

interface RuntimePromptCoordinatorDependencies {
  store: BridgeStore;
  codex: CodexRunner;
  runtimeAdapters: RuntimeAdapterRegistry;
  managedTerminals: ManagedTerminalRouter;
  opencode?: OpenCodeManager;
  approvals: ApprovalCoordinator;
  inputs: UserInputCoordinator;
  files: FileTransferCoordinator;
  activities: ActivityCoordinator;
  runtimeRetries: RuntimeRetryCoordinator;
  clearOpenCodeState: (sessionId: string) => void;
  respond: (
    sourceMessageId: string,
    chatId: string,
    text: string,
  ) => Promise<string | undefined>;
}

export class RuntimePromptCoordinator {
  private readonly inputLocks = new Set<string>();
  private readonly queues = new Map<string, QueuedRemotePrompt[]>();
  private readonly managedQueueDepth = new Map<string, number>();
  private readonly pendingPrompts = new Map<string, PendingRemotePrompt[]>();

  constructor(
    private readonly dependencies: RuntimePromptCoordinatorDependencies,
  ) {}

  queuedCount(sessionId: string): number {
    return (
      (this.queues.get(sessionId)?.length ?? 0) +
      (this.managedQueueDepth.get(sessionId) ?? 0)
    );
  }

  totalQueuedCount(): number {
    return (
      [...this.queues.values()].reduce(
        (total, queue) => total + queue.length,
        0,
      ) +
      [...this.managedQueueDepth.values()].reduce(
        (total, depth) => total + depth,
        0,
      )
    );
  }

  migrateSession(oldSessionId: string, newSessionId: string): void {
    if (this.inputLocks.delete(oldSessionId)) {
      this.inputLocks.add(newSessionId);
    }
    const queueDepth = this.managedQueueDepth.get(oldSessionId);
    if (queueDepth !== undefined) {
      this.managedQueueDepth.delete(oldSessionId);
      this.managedQueueDepth.set(newSessionId, queueDepth);
    }
    const pendingPrompts = this.pendingPrompts.get(oldSessionId);
    if (pendingPrompts) {
      this.pendingPrompts.delete(oldSessionId);
      this.pendingPrompts.set(newSessionId, pendingPrompts);
    }
  }

  clearSession(sessionId: string): void {
    this.inputLocks.delete(sessionId);
    this.queues.delete(sessionId);
    this.managedQueueDepth.delete(sessionId);
    this.pendingPrompts.delete(sessionId);
  }

  prepareStop(sessionId: string): void {
    if ((this.managedQueueDepth.get(sessionId) ?? 0) > 0) {
      this.inputLocks.add(sessionId);
    } else {
      this.inputLocks.delete(sessionId);
    }
  }

  decrementManagedQueue(sessionId: string): void {
    const current = this.managedQueueDepth.get(sessionId) ?? 0;
    if (current <= 1) {
      this.managedQueueDepth.delete(sessionId);
    } else {
      this.managedQueueDepth.set(sessionId, current - 1);
    }
  }

  releaseInputLock(sessionId: string): void {
    this.inputLocks.delete(sessionId);
  }

  isRuntimeReadyForRetry(session: SessionRecord): boolean {
    return this.dependencies.runtimeAdapters.forSession(session).isReady(session);
  }

  async sendRetry(session: SessionRecord, retryPrompt: string): Promise<void> {
    const runningSession = await this.dependencies.store.upsertSession({
      sessionId: session.sessionId,
      alias: session.alias,
      cwd: session.cwd,
      model: session.model,
      status: "running",
      source: session.source,
      runtime: session.runtime,
      managedTerminalId: session.managedTerminalId,
      managedTerminalElevated: session.managedTerminalElevated,
      managedByAssistant: session.managedByAssistant,
    });
    this.inputLocks.add(session.sessionId);
    this.rememberRemotePrompt(session.sessionId, retryPrompt);
    try {
      await this.dependencies.runtimeAdapters
        .requireCapability(runningSession.runtime ?? "codex", "prompt.send")
        .sendPrompt(runningSession, retryPrompt, "steer");
    } catch (error) {
      this.inputLocks.delete(session.sessionId);
      this.forgetRemotePrompt(session.sessionId, retryPrompt);
      throw error;
    }
  }

  releaseRetryLock(sessionId: string): void {
    this.inputLocks.delete(sessionId);
    const session = this.dependencies.store.getSession(sessionId);
    if (
      session &&
      runtimeDefinition(session.runtime).transport === "http_event_stream"
    ) {
      void this.drainOpenCodeQueue(sessionId);
      return;
    }
    void this.drainExternalQueue(sessionId);
  }

  consumeRemotePrompt(sessionId: string, prompt: string): boolean {
    const queue = this.pendingPrompts.get(sessionId);
    if (!queue) return false;
    const now = Date.now();
    const normalized = normalizePromptForMatch(prompt);
    const fresh = queue.filter((item) => now - item.createdAt <= 60_000);
    const index = fresh.findIndex((item) => item.prompt === normalized);
    if (index < 0) {
      if (fresh.length === 0) this.pendingPrompts.delete(sessionId);
      else this.pendingPrompts.set(sessionId, fresh);
      return false;
    }
    fresh.splice(index, 1);
    if (fresh.length === 0) this.pendingPrompts.delete(sessionId);
    else this.pendingPrompts.set(sessionId, fresh);
    return true;
  }

  async resume(
    session: SessionRecord,
    prompt: string,
    sourceMessageId: string,
    chatId: string,
    queueRequested = false,
    requestFileReturn = false,
  ): Promise<void> {
    const { store, inputs, runtimeRetries, managedTerminals, runtimeAdapters } =
      this.dependencies;
    if (store.hasPendingApprovalForSession(session.sessionId)) {
      await this.respond(
        sourceMessageId,
        chatId,
        codexNotReceived("请先处理待审批操作。"),
      );
      return;
    }
    if (inputs.hasPendingForSession(session.sessionId)) {
      await this.respond(
        sourceMessageId,
        chatId,
        codexNotReceived("请先回答待补充问题。"),
      );
      return;
    }
    await runtimeRetries.beginManualTurn(session.sessionId);
    if (runtimeDefinition(session.runtime).transport === "http_event_stream") {
      await this.resumeOpenCodeSession(
        session,
        prompt,
        sourceMessageId,
        chatId,
        requestFileReturn,
      );
      return;
    }
    const managedTerminal = managedTerminals.isManaged(session);
    if (managedTerminal && !runtimeAdapters.forSession(session).isReady(session)) {
      await this.respond(
        sourceMessageId,
        chatId,
        codexNotReceived("窗口尚未就绪。"),
      );
      return;
    }
    if (
      !managedTerminal &&
      (this.dependencies.codex.isRunning(session.sessionId) ||
        this.inputLocks.has(session.sessionId))
    ) {
      const queue = this.queues.get(session.sessionId) ?? [];
      queue.push({ prompt, sourceMessageId, chatId, requestFileReturn });
      this.queues.set(session.sessionId, queue);
      await this.respond(sourceMessageId, chatId, receivedText(session));
      return;
    }
    if (!managedTerminal) {
      this.inputLocks.add(session.sessionId);
    }

    try {
      const runningSession = await store.upsertSession({
        sessionId: session.sessionId,
        alias: session.alias,
        cwd: session.cwd,
        model: session.model,
        status: "running",
        source: session.source,
        managedTerminalId: session.managedTerminalId,
        managedTerminalElevated: session.managedTerminalElevated,
      });
      if (managedTerminal) {
        const busy =
          this.inputLocks.has(session.sessionId) || session.status === "running";
        const submitMode = queueRequested && busy ? "queue" : "steer";
        if (submitMode === "queue") {
          this.managedQueueDepth.set(
            session.sessionId,
            (this.managedQueueDepth.get(session.sessionId) ?? 0) + 1,
          );
        }
        this.inputLocks.add(session.sessionId);
        this.rememberRemotePrompt(session.sessionId, prompt);
        try {
          await runtimeAdapters
            .requireCapability(
              runningSession.runtime ?? "codex",
              submitMode === "queue" ? "prompt.queue" : "prompt.send",
            )
            .sendPrompt(runningSession, prompt, submitMode);
        } catch (error) {
          this.forgetRemotePrompt(session.sessionId, prompt);
          if (submitMode === "queue") {
            this.decrementManagedQueue(session.sessionId);
          }
          throw error;
        }
        if (requestFileReturn) {
          this.dependencies.files.registerReturnRequest(
            session.sessionId,
            chatId,
            submitMode === "queue"
              ? this.managedQueueDepth.get(session.sessionId) ?? 1
              : 0,
          );
        }
        const ackId = await this.respond(
          sourceMessageId,
          chatId,
          receivedText(runningSession),
        );
        if (ackId) {
          await this.addRoute(
            ackId,
            session.sessionId,
            chatId,
            "resume_ack",
          );
        }
        return;
      }
      await this.startExternalPrompt(runningSession, {
        prompt,
        sourceMessageId,
        chatId,
        requestFileReturn,
      });
    } catch (error) {
      this.inputLocks.delete(session.sessionId);
      const message = error instanceof Error ? error.message : String(error);
      await store.upsertSession({
        sessionId: session.sessionId,
        alias: session.alias,
        cwd: session.cwd,
        model: session.model,
        status: "error",
        error: message,
        source: session.source,
        managedTerminalId: session.managedTerminalId,
        managedTerminalElevated: session.managedTerminalElevated,
      });
      await this.respond(
        sourceMessageId,
        chatId,
        codexNotReceived(truncate(message, 160)),
      );
    }
  }

  async drainExternalQueue(sessionId: string): Promise<void> {
    if (
      this.dependencies.codex.isRunning(sessionId) ||
      this.inputLocks.has(sessionId)
    ) {
      return;
    }
    const queue = this.queues.get(sessionId);
    const item = queue?.shift();
    if (!item) {
      this.queues.delete(sessionId);
      return;
    }
    if (queue?.length === 0) {
      this.queues.delete(sessionId);
    }
    const session = this.dependencies.store.getSession(sessionId);
    if (!session || session.status === "ended") {
      await this.respond(
        item.sourceMessageId,
        item.chatId,
        "排队消息未执行：对应的外部 Codex 会话已经结束。",
      );
      return;
    }
    try {
      const runningSession = await this.dependencies.store.upsertSession({
        sessionId: session.sessionId,
        alias: session.alias,
        cwd: session.cwd,
        model: session.model,
        status: "running",
        source: session.source,
      });
      await this.startExternalPrompt(runningSession, item);
    } catch (error) {
      this.inputLocks.delete(sessionId);
      const message = error instanceof Error ? error.message : String(error);
      await this.respond(
        item.sourceMessageId,
        item.chatId,
        `排队消息启动失败：${truncate(message, 500)}`,
      );
      void this.drainExternalQueue(sessionId);
    }
  }

  async drainOpenCodeQueue(sessionId: string): Promise<void> {
    if (this.inputLocks.has(sessionId)) {
      return;
    }
    const queue = this.queues.get(sessionId);
    const item = queue?.shift();
    if (!item) {
      this.queues.delete(sessionId);
      return;
    }
    if (queue?.length === 0) {
      this.queues.delete(sessionId);
    }
    const session = this.dependencies.store.getSession(sessionId);
    if (
      !session ||
      runtimeDefinition(session.runtime).transport !== "http_event_stream" ||
      session.status === "ended"
    ) {
      await this.respond(
        item.sourceMessageId,
        item.chatId,
        notReceivedText(
          { runtime: "opencode" },
          "对应的 opencode 窗口已经关闭。",
        ),
      );
      return;
    }
    const runtimeAdapter = this.dependencies.runtimeAdapters.forSession(session);
    if (!runtimeAdapter.isReady(session)) {
      this.inputLocks.add(sessionId);
      await this.respond(
        item.sourceMessageId,
        item.chatId,
        notReceivedText(session, "opencode 窗口未连接。"),
      );
      this.inputLocks.delete(sessionId);
      void this.drainOpenCodeQueue(sessionId);
      return;
    }
    this.inputLocks.add(sessionId);
    try {
      const runningSession = await this.dependencies.store.upsertSession({
        sessionId: session.sessionId,
        alias: session.alias,
        cwd: session.cwd,
        model: session.model,
        status: "running",
        runtime: "opencode",
        managedByAssistant: true,
      });
      this.rememberRemotePrompt(session.sessionId, item.prompt);
      await this.dependencies.runtimeAdapters
        .requireCapability("opencode", "prompt.send")
        .sendPrompt(runningSession, item.prompt, "steer");
      if (item.requestFileReturn) {
        this.dependencies.files.registerReturnRequest(
          session.sessionId,
          item.chatId,
          0,
        );
      }
      const ackId = await this.respond(
        item.sourceMessageId,
        item.chatId,
        receivedText(runningSession),
      );
      if (ackId) {
        await this.addRoute(
          ackId,
          session.sessionId,
          item.chatId,
          "resume_ack",
        );
      }
    } catch (error) {
      this.inputLocks.delete(sessionId);
      this.forgetRemotePrompt(sessionId, item.prompt);
      const message = error instanceof Error ? error.message : String(error);
      await this.respond(
        item.sourceMessageId,
        item.chatId,
        notReceivedText(session, truncate(message, 500)),
      );
      void this.drainOpenCodeQueue(sessionId);
    }
  }

  async forgetOpenCodeSession(
    sessionId: string,
    reason: string,
  ): Promise<void> {
    const { inputs, approvals, store, files, runtimeRetries, activities } =
      this.dependencies;
    await inputs.resolveForSession(sessionId, "local");
    await approvals.resolveForSession(sessionId);
    const session = store.getSession(sessionId);
    if (session && session.status !== "ended") {
      await store.upsertSession({
        sessionId,
        cwd: session.cwd,
        model: session.model,
        status: "ended",
        runtime: "opencode",
        managedByAssistant: true,
      });
    }
    this.inputLocks.delete(sessionId);
    const queued = this.queues.get(sessionId);
    this.queues.delete(sessionId);
    this.pendingPrompts.delete(sessionId);
    files.removeSession(sessionId);
    runtimeRetries.reset(sessionId);
    this.dependencies.clearOpenCodeState(sessionId);
    if (session) {
      void activities.finish(sessionId, reason).catch((error) => {
        console.warn("[opencode] Could not finalize activity:", error);
      });
    }
    this.dependencies.opencode?.forgetSession(sessionId);
    if (queued) {
      for (const item of queued) {
        await this.respond(
          item.sourceMessageId,
          item.chatId,
          notReceivedText({ runtime: "opencode" }, reason),
        );
      }
    }
  }

  private async resumeOpenCodeSession(
    session: SessionRecord,
    prompt: string,
    sourceMessageId: string,
    chatId: string,
    requestFileReturn: boolean,
  ): Promise<void> {
    const runtimeAdapter = this.dependencies.runtimeAdapters.forSession(session);
    if (!runtimeAdapter.isReady(session)) {
      await this.respond(
        sourceMessageId,
        chatId,
        notReceivedText(session, "opencode 窗口未连接。"),
      );
      return;
    }
    if (this.inputLocks.has(session.sessionId) || session.status === "running") {
      const queue = this.queues.get(session.sessionId) ?? [];
      queue.push({ prompt, sourceMessageId, chatId, requestFileReturn });
      this.queues.set(session.sessionId, queue);
      await this.respond(sourceMessageId, chatId, receivedText(session));
      return;
    }
    this.inputLocks.add(session.sessionId);
    try {
      const runningSession = await this.dependencies.store.upsertSession({
        sessionId: session.sessionId,
        alias: session.alias,
        cwd: session.cwd,
        model: session.model,
        status: "running",
        source: "opencode",
        runtime: "opencode",
        managedByAssistant: true,
      });
      this.rememberRemotePrompt(session.sessionId, prompt);
      await this.dependencies.runtimeAdapters
        .requireCapability("opencode", "prompt.send")
        .sendPrompt(runningSession, prompt, "steer");
      if (requestFileReturn) {
        this.dependencies.files.registerReturnRequest(
          session.sessionId,
          chatId,
          0,
        );
      }
      const ackId = await this.respond(
        sourceMessageId,
        chatId,
        receivedText(runningSession),
      );
      if (ackId) {
        await this.addRoute(
          ackId,
          session.sessionId,
          chatId,
          "resume_ack",
        );
      }
    } catch (error) {
      this.inputLocks.delete(session.sessionId);
      this.forgetRemotePrompt(session.sessionId, prompt);
      const message = error instanceof Error ? error.message : String(error);
      await this.dependencies.store.upsertSession({
        sessionId: session.sessionId,
        alias: session.alias,
        cwd: session.cwd,
        model: session.model,
        status: "error",
        error: message,
        runtime: "opencode",
        managedByAssistant: true,
      });
      await this.respond(
        sourceMessageId,
        chatId,
        notReceivedText(session, truncate(message, 160)),
      );
    }
  }

  private async startExternalPrompt(
    session: SessionRecord,
    item: QueuedRemotePrompt,
  ): Promise<void> {
    this.inputLocks.add(session.sessionId);
    await this.dependencies.codex.resume(session, item.prompt, async (result) => {
      const retrying = await this.handleCodexExit(session, result, item);
      if (!retrying) {
        await this.drainExternalQueue(session.sessionId);
      }
    });
    if (item.requestFileReturn && item.retryAttempt === undefined) {
      this.dependencies.files.registerReturnRequest(
        session.sessionId,
        item.chatId,
        0,
      );
    }
    if (item.retryAttempt === undefined) {
      const ackId = await this.respond(
        item.sourceMessageId,
        item.chatId,
        receivedText(session),
      );
      if (ackId) {
        await this.addRoute(
          ackId,
          session.sessionId,
          item.chatId,
          "resume_ack",
        );
      }
    }
  }

  private async handleCodexExit(
    session: SessionRecord,
    result: CodexExitResult,
    item: QueuedRemotePrompt,
  ): Promise<boolean> {
    if (result.code === 0) {
      this.inputLocks.delete(session.sessionId);
      this.dependencies.runtimeRetries.reset(session.sessionId);
      return false;
    }
    const reason =
      result.stderr ||
      (result.signal
        ? `Codex 进程被信号 ${result.signal} 终止。`
        : `Codex 进程退出，代码 ${String(result.code)}。`);
    const retrying = await this.dependencies.runtimeRetries.notifyTurnError(
      session,
      `external-${randomUUID()}`,
      reason,
      undefined,
      {
        isReady: (current) =>
          current.status !== "ended" &&
          !this.dependencies.codex.isRunning(current.sessionId),
        sendRetry: async (current) => {
          const runningSession = await this.dependencies.store.upsertSession({
            sessionId: current.sessionId,
            alias: current.alias,
            cwd: current.cwd,
            model: current.model,
            status: "running",
            source: current.source,
          });
          await this.startExternalPrompt(runningSession, {
            ...item,
            retryAttempt: (item.retryAttempt ?? 0) + 1,
          });
        },
      },
    );
    if (!retrying) {
      this.inputLocks.delete(session.sessionId);
    }
    return retrying;
  }

  private rememberRemotePrompt(sessionId: string, prompt: string): void {
    const now = Date.now();
    const queue = (this.pendingPrompts.get(sessionId) ?? []).filter(
      (item) => now - item.createdAt <= 60_000,
    );
    queue.push({ prompt: normalizePromptForMatch(prompt), createdAt: now });
    this.pendingPrompts.set(sessionId, queue.slice(-12));
  }

  private forgetRemotePrompt(sessionId: string, prompt: string): void {
    const queue = this.pendingPrompts.get(sessionId);
    if (!queue) return;
    const normalized = normalizePromptForMatch(prompt);
    const index = queue.findIndex((item) => item.prompt === normalized);
    if (index >= 0) queue.splice(index, 1);
    if (queue.length === 0) this.pendingPrompts.delete(sessionId);
  }

  private async respond(
    sourceMessageId: string,
    chatId: string,
    text: string,
  ): Promise<string | undefined> {
    return await this.dependencies.respond(sourceMessageId, chatId, text);
  }

  private async addRoute(
    messageId: string,
    sessionId: string,
    chatId: string,
    kind: "resume_ack",
  ): Promise<void> {
    await this.dependencies.store.addMessageRoute({
      messageId,
      sessionId,
      chatId,
      kind,
      createdAt: new Date().toISOString(),
    });
  }
}

function normalizePromptForMatch(value: string): string {
  return value.normalize("NFC").replace(/\s+/gu, " ").trim();
}

function codexNotReceived(reason: string): string {
  return notReceivedText(undefined, reason);
}

function receivedText(
  session: Pick<SessionRecord, "runtime"> | undefined,
): string {
  return runtimeReceivedText(session?.runtime);
}

function notReceivedText(
  session: Pick<SessionRecord, "runtime"> | undefined,
  reason: string,
): string {
  return `${runtimeDisplayName(session?.runtime)} 未接收：${reason}`;
}
