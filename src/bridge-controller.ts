import { stat } from "node:fs/promises";
import path from "node:path";

import { ApprovalCoordinator } from "./approval-coordinator.js";
import {
  CardActionHandler,
  type CardActionResult,
} from "./card-action-handler.js";
import { buildStopCards } from "./cards.js";
import { ActivityCoordinator } from "./activity-coordinator.js";
import {
  CodexTranscriptMonitor,
  type CodexTranscriptErrorEvent,
} from "./codex-transcript-monitor.js";
import { CodexRunner } from "./codex-runner.js";
import {
  ManagedTerminalRouter,
  managedTerminalSessionId,
} from "./managed-terminal.js";
import { OpenCodeManager } from "./opencode-manager.js";
import { OpenCodeInteractionCoordinator } from "./opencode-interaction-coordinator.js";
import { OpenCodeEventCoordinator } from "./opencode-event-coordinator.js";
import type {
  OpenCodeMessage,
  OpenCodeMessagePartUpdatedProperties,
  OpenCodePermission,
  OpenCodePermissionReplied,
  OpenCodeQuestionRejected,
  OpenCodeQuestionReplied,
  OpenCodeQuestionRequest,
  OpenCodeSession,
} from "./opencode-client.js";
import {
  type ClientProcessMetadata,
} from "./process-tracking.js";
import type {
  ActivityHookPayload,
  BridgeSettings,
  MessageRouteKind,
  PermissionHookPayload,
  RequestUserInputHookPayload,
  SessionEndHookPayload,
  SessionRecord,
  SessionStartHookPayload,
  StopHookPayload,
} from "./domain.js";
import {
  runtimeDisplayName,
  statusLabel,
  stringifyModel,
} from "./domain.js";
import { FeishuGateway } from "./feishu.js";
import { FeishuMessageHandler } from "./feishu-message-handler.js";
import { extractBridgeFileDirectives } from "./file-transfer.js";
import { FileTransferCoordinator } from "./file-transfer-coordinator.js";
import { HookEventCoordinator } from "./hook-event-coordinator.js";
import { BridgeStore } from "./store.js";
import { RuntimeRetryCoordinator } from "./runtime-retry-coordinator.js";
import { RuntimeLaunchCoordinator } from "./runtime-launch-coordinator.js";
import { RuntimePromptCoordinator } from "./runtime-prompt-coordinator.js";
import { SessionGroupCoordinator } from "./session-group-coordinator.js";
import { SessionDirectory } from "./session-directory.js";
import { UserInputCoordinator } from "./user-input-coordinator.js";
import {
  TurnNotificationCoordinator,
  turnNotificationWasSent,
} from "./turn-notification-coordinator.js";

type FeishuEvent = Record<string, any>;

interface ControllerConfig {
  bindCommand: string;
  approvalTimeoutMs: number;
  inputTimeoutMs: number;
  sessionActiveMs: number;
  sessionGroupInactiveMs?: number;
  runtimeLaunchTimeoutMs?: number;
  uploadsDirectory: string;
  inboundFileMaxBytes: number;
  inboundAttachmentMaxCount: number;
  uploadMaxFiles: number;
  uploadMaxBytes: number;
  uploadTtlMs: number;
  outboundFileMaxBytes: number;
  retryBaseDelayMs?: number;
  transcriptPollIntervalMs?: number;
  transcriptIdlePollIntervalMs?: number;
  transcriptActiveWindowMs?: number;
  approvalLogPath?: string;
  approvalLogMaxBytes?: number;
  approvalLogMaxBackups?: number;
  liveClientProcessIds?: (clients: ClientProcessMetadata[]) => ReadonlySet<number>;
}

export class BridgeController {
  private readonly approvals: ApprovalCoordinator;
  private readonly inputs: UserInputCoordinator;
  private readonly cardActions: CardActionHandler;
  private readonly feishuMessages: FeishuMessageHandler;
  private readonly hookEvents: HookEventCoordinator;
  private readonly sessionGroups: SessionGroupCoordinator;
  private readonly sessionDirectory: SessionDirectory;
  private readonly turnNotifications: TurnNotificationCoordinator;
  private readonly activities: ActivityCoordinator;
  private readonly files: FileTransferCoordinator;
  private readonly runtimeRetries: RuntimeRetryCoordinator;
  private readonly runtimePrompts: RuntimePromptCoordinator;
  private readonly runtimeLaunches: RuntimeLaunchCoordinator;
  private readonly opencodeInteractions: OpenCodeInteractionCoordinator;
  private readonly opencodeEvents: OpenCodeEventCoordinator;
  private readonly transcriptMonitor: CodexTranscriptMonitor;
  private closing = false;
  private closePromise: Promise<void> | undefined;

  constructor(
    private readonly store: BridgeStore,
    private readonly feishu: FeishuGateway,
    private readonly codex: CodexRunner,
    private readonly managedTerminals: ManagedTerminalRouter,
    private readonly opencode: OpenCodeManager | undefined,
    private readonly config: ControllerConfig,
  ) {
    this.files = new FileTransferCoordinator({
      feishu,
      uploadsDirectory: config.uploadsDirectory,
      inboundFileMaxBytes: config.inboundFileMaxBytes,
      inboundAttachmentMaxCount: config.inboundAttachmentMaxCount,
      uploadMaxFiles: config.uploadMaxFiles,
      uploadMaxBytes: config.uploadMaxBytes,
      uploadTtlMs: config.uploadTtlMs,
      outboundFileMaxBytes: config.outboundFileMaxBytes,
      addRoute: (messageId, sessionId, chatId, kind) =>
        this.addRoute(messageId, sessionId, chatId, kind),
    });
    this.transcriptMonitor = new CodexTranscriptMonitor(
      (event) => this.handleCodexTranscriptError(event),
      {
        activePollIntervalMs: config.transcriptPollIntervalMs,
        idlePollIntervalMs: config.transcriptIdlePollIntervalMs,
        activeWindowMs: config.transcriptActiveWindowMs,
      },
    );
    this.sessionGroups = new SessionGroupCoordinator(
      store,
      feishu,
      config.sessionGroupInactiveMs,
    );
    this.sessionDirectory = new SessionDirectory({
      store,
      managedTerminals,
      opencode,
      sessionActiveMs: config.sessionActiveMs,
      sessionGroups: this.sessionGroups,
      liveClientProcessIds: config.liveClientProcessIds,
      queuedPromptCount: (sessionId) =>
        this.runtimePrompts.queuedCount(sessionId),
      respond: (sourceMessageId, chatId, text) =>
        this.respond(sourceMessageId, chatId, text),
    });
    this.turnNotifications = new TurnNotificationCoordinator({
      store,
      feishu,
      recipients: (session) =>
        this.sessionGroups.notificationRecipients(session),
      addRoute: (messageId, sessionId, chatId, kind) =>
        this.addRoute(messageId, sessionId, chatId, kind),
    });
    this.activities = new ActivityCoordinator({
      store,
      feishu,
      recipients: (session) =>
        this.sessionGroups.notificationRecipients(session),
      addRoute: (messageId, sessionId, chatId, kind) =>
        this.addRoute(messageId, sessionId, chatId, kind),
      watchSession: (session) => this.watchCodexTranscript(session),
    });
    this.runtimeLaunches = new RuntimeLaunchCoordinator({
      store,
      managedTerminals,
      opencode,
      timeoutMs: config.runtimeLaunchTimeoutMs,
      respond: (sourceMessageId, chatId, text) =>
        this.respond(sourceMessageId, chatId, text),
      resume: (session, item) =>
        this.runtimePrompts.resume(
          session,
          item.prompt,
          item.sourceMessageId,
          item.chatId,
          item.queueRequested,
          item.requestFileReturn,
        ),
    });
    this.approvals = new ApprovalCoordinator({
      store,
      feishu,
      opencode,
      config,
      notificationRecipients: (session) =>
        this.sessionGroups.notificationRecipients(session),
      onOpenCodePermissionForwarded: (sessionId, permissionId) => {
        this.opencodeInteractions.releasePermissionClaim(sessionId, permissionId);
      },
    });
    this.inputs = new UserInputCoordinator({
      store,
      feishu,
      opencode,
      inputTimeoutMs: config.inputTimeoutMs,
      onOpenCodeQuestionAnswered: (sessionId, requestId) => {
        this.opencodeInteractions.releaseQuestionClaim(sessionId, requestId);
      },
    });
    this.opencodeInteractions = new OpenCodeInteractionCoordinator({
      store,
      opencode,
      approvals: this.approvals,
      inputs: this.inputs,
      sessionGroups: this.sessionGroups,
      approvalTimeoutMs: config.approvalTimeoutMs,
      isClosing: () => this.closing,
    });
    this.runtimeRetries = new RuntimeRetryCoordinator({
      store,
      retryBaseDelayMs: config.retryBaseDelayMs,
      finishActivity: (sessionId, label) =>
        this.activities.finish(sessionId, label),
      isRuntimeReady: (session) =>
        this.runtimePrompts.isRuntimeReadyForRetry(session),
      sendRetry: (session, prompt) =>
        this.runtimePrompts.sendRetry(session, prompt),
      sendErrorNotification: (session, turnId, errorMessage, cards) =>
        this.turnNotifications.send(
          session,
          turnId,
          "error",
          errorMessage,
          cards,
          "[error] Failed to send a runtime error card:",
        ),
      patchCard: (messageId, card) => this.feishu.patchCard(messageId, card),
      releaseRetryLock: (sessionId) =>
        this.runtimePrompts.releaseRetryLock(sessionId),
    });
    this.runtimePrompts = new RuntimePromptCoordinator({
      store,
      codex,
      managedTerminals,
      opencode,
      approvals: this.approvals,
      inputs: this.inputs,
      files: this.files,
      activities: this.activities,
      runtimeRetries: this.runtimeRetries,
      clearOpenCodeState: (sessionId) => {
        this.opencodeEvents.clearSession(sessionId);
        this.opencodeInteractions.clearSession(sessionId);
      },
      respond: (sourceMessageId, chatId, text) =>
        this.respond(sourceMessageId, chatId, text),
    });
    this.opencodeEvents = new OpenCodeEventCoordinator({
      store,
      feishu,
      opencode,
      sessionGroups: this.sessionGroups,
      runtimeLaunches: this.runtimeLaunches,
      runtimeRetries: this.runtimeRetries,
      inputs: this.inputs,
      activities: this.activities,
      turnNotifications: this.turnNotifications,
      files: this.files,
      releaseRemoteInputLock: (sessionId) =>
        this.runtimePrompts.releaseInputLock(sessionId),
      drainQueue: (sessionId) =>
        this.runtimePrompts.drainOpenCodeQueue(sessionId),
      forgetSession: (sessionId, reason) =>
        this.runtimePrompts.forgetOpenCodeSession(sessionId, reason),
      consumeRemotePrompt: (sessionId, prompt) =>
        this.runtimePrompts.consumeRemotePrompt(sessionId, prompt),
      addRoute: (messageId, sessionId, chatId, kind) =>
        this.addRoute(messageId, sessionId, chatId, kind),
    });
    this.cardActions = new CardActionHandler(
      store,
      this.approvals,
      this.inputs,
      this.runtimeRetries,
      this.runtimeLaunches,
    );
    this.feishuMessages = new FeishuMessageHandler({
      store,
      bindCommand: config.bindCommand,
      files: this.files,
      runtimeLaunches: this.runtimeLaunches,
      sessionDirectory: this.sessionDirectory,
      inputs: this.inputs,
      approvals: this.approvals,
      managedTerminals,
      opencode,
      queuedPromptCount: () => this.runtimePrompts.totalQueuedCount(),
      initializeSessionGroups: () => this.initializeSessionGroups(),
      respond: (sourceMessageId, chatId, text) =>
        this.respond(sourceMessageId, chatId, text),
      respondCard: (sourceMessageId, chatId, card) =>
        this.respondCard(sourceMessageId, chatId, card),
      resumeSession: (
        session,
        prompt,
        sourceMessageId,
        chatId,
        queueRequested,
        requestFileReturn,
      ) =>
        this.runtimePrompts.resume(
          session,
          prompt,
          sourceMessageId,
          chatId,
          queueRequested,
          requestFileReturn,
        ),
    });
    this.hookEvents = new HookEventCoordinator({
      store,
      feishu,
      managedTerminals,
      transcriptMonitor: this.transcriptMonitor,
      files: this.files,
      activities: this.activities,
      sessionGroups: this.sessionGroups,
      runtimeLaunches: this.runtimeLaunches,
      approvals: this.approvals,
      inputs: this.inputs,
      runtimeRetries: this.runtimeRetries,
      turnNotifications: this.turnNotifications,
      approvalTimeoutMs: config.approvalTimeoutMs,
      inputTimeoutMs: config.inputTimeoutMs,
      isClosing: () => this.closing,
      watchTranscript: (session) => this.watchCodexTranscript(session),
      migratePromptState: (oldSessionId, newSessionId) =>
        this.runtimePrompts.migrateSession(oldSessionId, newSessionId),
      clearPromptState: (sessionId) =>
        this.runtimePrompts.clearSession(sessionId),
      prepareStop: (sessionId) => this.runtimePrompts.prepareStop(sessionId),
      decrementManagedQueueDepth: (sessionId) =>
        this.runtimePrompts.decrementManagedQueue(sessionId),
      drainExternalQueue: (sessionId) =>
        this.runtimePrompts.drainExternalQueue(sessionId),
      consumeRemotePrompt: (sessionId, prompt) =>
        this.runtimePrompts.consumeRemotePrompt(sessionId, prompt),
      addRoute: (messageId, sessionId, chatId, kind) =>
        this.addRoute(messageId, sessionId, chatId, kind),
    });
  }

  async initialize(): Promise<void> {
    await this.initializeCodexTranscriptMonitors();
    await this.initializeSessionGroups();
    await this.recoverPendingTurnNotifications();
  }

  async close(): Promise<void> {
    if (this.closePromise) {
      return this.closePromise;
    }
    this.closing = true;
    this.closePromise = this.closeInternal();
    return this.closePromise;
  }

  private async closeInternal(): Promise<void> {
    await this.transcriptMonitor.close();
    await this.resolvePendingInteractionsForShutdown();
    this.inputs.dispose();
    this.runtimeLaunches.dispose();
    this.activities.dispose();
    this.runtimeRetries.dispose();
    await this.approvals.dispose();
  }

  private async resolvePendingInteractionsForShutdown(): Promise<void> {
    for (let attempt = 0; attempt < 3; attempt += 1) {
      await this.approvals.awaitActiveCompletions();
      await this.inputs.resolveAllForShutdown();
      await this.approvals.resolveAllForShutdown();
      if (
        this.inputs.pendingCount === 0 &&
        !this.approvals.hasPendingApprovals()
      ) {
        return;
      }
    }
  }

  private async initializeCodexTranscriptMonitors(): Promise<void> {
    for (const session of this.store.listOpenSessions()) {
      await this.watchCodexTranscript(session);
    }
  }

  async initializeSessionGroups(): Promise<void> {
    await this.sessionGroups.initialize();
  }

  async cleanupInactiveSessionGroups(
    now = Date.now(),
  ): Promise<{ deleted: number; failed: number }> {
    return await this.sessionGroups.cleanup(now);
  }

  handleRuntimeLaunchClaim(): Record<string, unknown> {
    return this.runtimeLaunches.claim();
  }

  async handleRuntimeLaunchComplete(
    value: Record<string, unknown>,
  ): Promise<Record<string, unknown>> {
    return await this.runtimeLaunches.complete(value);
  }

  /**
   * Direct in-process callers get the full status by default. Network callers
   * must explicitly prove local control; unauthenticated /health only needs a
   * liveness result and must not expose sessions, approvals, or the pairing code.
   */
  health(includeLocalDetails = true): Record<string, unknown> {
    if (!includeLocalDetails) {
      return { ok: true };
    }
    return this.buildHealth(this.sessionDirectory.listActive());
  }

  async refreshHealth(includeLocalDetails = true): Promise<Record<string, unknown>> {
    if (!includeLocalDetails) {
      return { ok: true };
    }
    return this.buildHealth(await this.sessionDirectory.refreshActive());
  }

  private buildHealth(sessions: SessionRecord[]): Record<string, unknown> {
    const displayedSessions = [...sessions].sort(
      (left, right) =>
        left.openedAt.localeCompare(right.openedAt) ||
        left.sessionId.localeCompare(right.sessionId),
    );
    const activeSessionIds = new Set(sessions.map((session) => session.sessionId));
    const historySessions = this.store
      .listHistorySessions()
      .filter(
        (session) =>
          !activeSessionIds.has(session.sessionId) &&
          !this.managedTerminals.isOnline(session),
      );
    const approvals = this.approvals.listViews();
    const pairingCode = this.store.getPairingCode();
    const sessionView = (session: SessionRecord) => ({
      sessionId: session.sessionId,
      shortId: session.shortId,
      alias: session.alias ?? "",
      projectName: session.projectName,
      cwd: session.cwd,
      model: stringifyModel(session.model),
      status: session.status,
      statusLabel: statusLabel(session.status),
      source: session.source ?? "",
      runtime: session.runtime ?? "codex",
      openedAt: session.openedAt,
      lastSeenAt: session.lastSeenAt,
      endedAt: session.endedAt ?? "",
      remoteResumeRunning: this.codex.isRunning(session.sessionId),
      externalProcessTracked: Boolean(session.clientProcessId),
      managedTerminal: this.managedTerminals.isManaged(session),
      managedTerminalElevated: session.managedTerminalElevated === true,
      managedTerminalOnline: this.managedTerminals.isOnline(session),
      managedTerminalReady: this.managedTerminals.isReady(session),
      managedByAssistant: session.managedByAssistant === true,
      feishuChatId: session.feishuChatId ?? "",
      feishuChatName: session.feishuChatName ?? "",
      feishuChatStatus: session.feishuChatId
        ? "connected"
        : session.managedByAssistant === true
          ? session.feishuChatError
            ? "error"
            : "pending"
          : "not_applicable",
      feishuChatError: session.feishuChatError ?? "",
      queuedPrompts: this.runtimePrompts.queuedCount(session.sessionId),
    });
    return {
      ok: true,
      bindings: this.store.listBindings().length,
      ownerConfigured: Boolean(this.store.getOwnerOpenId()),
      pairingCode: pairingCode ?? "",
      bindingCommand: pairingCode
        ? `${this.config.bindCommand} ${pairingCode}`
        : "",
      activeSessions: sessions.length,
      pendingApprovals: approvals.filter(
        (approval) =>
          approval.status === "pending" &&
          approval.requiresManualApproval === true,
      ).length,
      pendingDesktopApprovals: approvals.filter(
        (approval) =>
          approval.status === "pending" &&
          approval.desktopApprovalRequested === true,
      ).length,
      approvals,
      pendingInputs: this.inputs.pendingCount,
      queuedPrompts:
        this.runtimePrompts.totalQueuedCount() +
        this.runtimeLaunches.queuedPromptCount,
      runningResumes: sessions.filter((session) => this.codex.isRunning(session.sessionId))
        .length,
      pendingRuntimeLaunches: this.runtimeLaunches.pendingCount,
      opencodeInstances: this.opencode?.listInstances().length ?? 0,
      activeSessionDefinition: this.sessionDirectory.activeDefinition(),
      settings: this.store.getSettings(),
      sessions: displayedSessions.map(sessionView),
      historySessions: historySessions.map(sessionView),
    };
  }

  handleManagedTerminalRegistration(value: Record<string, unknown>): Record<string, unknown> {
    const terminalId = typeof value.terminalId === "string" ? value.terminalId : "";
    const existingSession = terminalId
      ? this.store.findSessionByManagedTerminalId(terminalId)
      : undefined;
    this.managedTerminals.register(
      value,
      existingSession?.source === "managed_window"
        ? undefined
        : existingSession?.sessionId,
    );
    return { ok: true };
  }

  async handleManagedTerminalUnregistration(
    value: Record<string, unknown>,
  ): Promise<Record<string, unknown>> {
    const terminalId = typeof value.terminalId === "string" ? value.terminalId : "";
    this.managedTerminals.unregister(value);
    const session = terminalId
      ? this.store.findSessionByManagedTerminalId(terminalId)
      : undefined;
    if (terminalId) {
      this.runtimePrompts.clearSession(managedTerminalSessionId(terminalId));
    }
    if (session) {
      await this.transcriptMonitor.unwatch(session.sessionId);
      this.runtimePrompts.clearSession(session.sessionId);
      this.files.removeSession(session.sessionId);
      this.runtimeRetries.reset(session.sessionId);
      await this.inputs.resolveForSession(session.sessionId, "local");
      await this.approvals.resolveForSession(session.sessionId);
      void this.activities.finish(session.sessionId, "窗口已关闭");
      await this.store.upsertSession({
        sessionId: session.sessionId,
        alias: session.alias,
        cwd: session.cwd,
        model: session.model,
        status: "ended",
        managedTerminalId: session.managedTerminalId,
        managedTerminalElevated: session.managedTerminalElevated,
        managedByAssistant: true,
      });
    }
    return { ok: true };
  }

  async handleSessionAliasUpdate(
    value: Record<string, unknown>,
  ): Promise<Record<string, unknown>> {
    return await this.sessionDirectory.handleAliasUpdate(value);
  }

  async handleSessionGroupRetry(
    value: Record<string, unknown>,
  ): Promise<Record<string, unknown>> {
    const sessionId = typeof value.sessionId === "string" ? value.sessionId.trim() : "";
    if (!sessionId || sessionId.length > 256) {
      return { ok: false, error: "会话 ID 参数不正确。" };
    }
    return await this.sessionGroups.retry(sessionId);
  }

  async handleSessionHistoryHide(
    value: Record<string, unknown>,
  ): Promise<Record<string, unknown>> {
    const sessionId = typeof value.sessionId === "string" ? value.sessionId.trim() : "";
    if (!sessionId || sessionId.length > 256) {
      return { ok: false, error: "会话 ID 参数不正确。" };
    }

    const session = await this.store.hideSessionFromHistory(sessionId);
    return session
      ? { ok: true, sessionId: session.sessionId }
      : { ok: false, error: "历史记录不存在，或不是由助手创建的会话。" };
  }

  async handleLocalApproval(
    value: Record<string, unknown>,
  ): Promise<Record<string, unknown>> {
    return await this.approvals.handleLocalApproval(value);
  }

  async handleSettingsUpdate(
    value: Record<string, unknown>,
  ): Promise<Record<string, unknown>> {
    const booleanKeys = [
      "notifyActivity",
      "notifyUserPrompts",
      "autoRetryErrors",
      "autoApprove",
      "notifyAutoApprovals",
    ] as const;
    const update: Record<string, boolean | number | string> = {};
    if (value.workspaceRoot !== undefined) {
      if (
        typeof value.workspaceRoot !== "string" ||
        !value.workspaceRoot.trim() ||
        value.workspaceRoot.length > 1024 ||
        !path.isAbsolute(value.workspaceRoot.trim())
      ) {
        return { ok: false, error: "默认工作区必须是有效的绝对目录。" };
      }
      const workspaceRoot = path.resolve(value.workspaceRoot.trim());
      try {
        if (!(await stat(workspaceRoot)).isDirectory()) {
          return { ok: false, error: "默认工作区不是文件夹。" };
        }
      } catch {
        return { ok: false, error: "默认工作区不存在或无法访问。" };
      }
      update.workspaceRoot = workspaceRoot;
    }
    for (const key of booleanKeys) {
      if (value[key] !== undefined) {
        if (typeof value[key] !== "boolean") {
          return { ok: false, error: "设置值必须是开关状态。" };
        }
        update[key] = value[key];
      }
    }
    const numberSettings = [
      ["retryMaxAttempts", 1, 20],
      ["retryIntervalSeconds", 1, 600],
      ["retryJitterSeconds", 0, 120],
    ] as const;
    for (const [key, minimum, maximum] of numberSettings) {
      if (value[key] === undefined) continue;
      if (
        typeof value[key] !== "number" ||
        !Number.isSafeInteger(value[key]) ||
        value[key] < minimum ||
        value[key] > maximum
      ) {
        return {
          ok: false,
          error: `${key} 必须是 ${minimum} 到 ${maximum} 之间的整数。`,
        };
      }
      update[key] = value[key];
    }
    if (Object.keys(update).length === 0) {
      return { ok: false, error: "没有可保存的设置。" };
    }
    return {
      ok: true,
      settings: await this.store.updateSettings(update as Partial<BridgeSettings>),
    };
  }

  async handleOpenCodeLaunch(
    value: Record<string, unknown>,
  ): Promise<Record<string, unknown>> {
    const cwd = typeof value.cwd === "string" ? value.cwd.trim() : "";
    if (!cwd || cwd.length > 1024) {
      return { ok: false, error: "目录参数不正确。" };
    }
    const sessionId = typeof value.sessionId === "string" ? value.sessionId.trim() : "";
    if (sessionId.length > 512) {
      return { ok: false, error: "会话参数不正确。" };
    }
    if (!this.opencode) {
      return { ok: false, error: "opencode 支持未启用。" };
    }
    try {
      const result = await this.opencode.launch(cwd, sessionId || undefined);
      console.log(
        `[opencode] launch cwd=${cwd} sessionId=${sessionId || "(none)"} port=${result.port}`,
      );
      return { ok: true, port: result.port, cwd };
    } catch (error) {
      return { ok: false, error: error instanceof Error ? error.message : String(error) };
    }
  }

  async handleOpenCodeRegister(
    value: Record<string, unknown>,
  ): Promise<Record<string, unknown>> {
    const port = typeof value.port === "number" ? value.port : Number(value.port);
    const cwd = typeof value.cwd === "string" ? value.cwd.trim() : "";
    if (!Number.isSafeInteger(port) || port <= 0 || port > 65535) {
      return { ok: false, error: "端口参数不正确。" };
    }
    if (!cwd || cwd.length > 1024) {
      return { ok: false, error: "目录参数不正确。" };
    }
    if (!this.opencode) {
      return { ok: false, error: "opencode 支持未启用。" };
    }
    try {
      await this.opencode.register(port, cwd);
      return { ok: true, port, cwd };
    } catch (error) {
      return { ok: false, error: error instanceof Error ? error.message : String(error) };
    }
  }

  async handleOpenCodeUnregister(
    value: Record<string, unknown>,
  ): Promise<Record<string, unknown>> {
    const port = typeof value.port === "number" ? value.port : Number(value.port);
    if (!Number.isSafeInteger(port) || port <= 0 || port > 65535) {
      return { ok: false, error: "端口参数不正确。" };
    }
    if (!this.opencode) {
      return { ok: false, error: "opencode 支持未启用。" };
    }
    await this.opencode.unregister(port);
    return { ok: true, port };
  }

  async handleOpenCodeSessionCreated(session: OpenCodeSession): Promise<void> {
    await this.opencodeEvents.handleSessionCreated(session);
  }

  async handleOpenCodeSessionDeleted(sessionId: string): Promise<void> {
    await this.opencodeEvents.handleSessionDeleted(sessionId);
  }

  async handleOpenCodeSessionStatus(sessionId: string, status: string): Promise<void> {
    await this.opencodeEvents.handleSessionStatus(sessionId, status);
  }

  async handleOpenCodeSessionCompacted(sessionId: string): Promise<void> {
    await this.opencodeEvents.handleSessionCompacted(sessionId);
  }

  async handleOpenCodeSessionIdle(sessionId: string): Promise<void> {
    await this.opencodeEvents.handleSessionIdle(sessionId);
  }

  async handleOpenCodeSessionError(
    sessionId: string,
    error: string | undefined,
  ): Promise<void> {
    await this.opencodeEvents.handleSessionError(sessionId, error);
  }

  async handleOpenCodeInstanceDisconnected(port: number): Promise<void> {
    await this.opencodeEvents.handleInstanceDisconnected(port);
  }

  async handleOpenCodePermissionUpdated(permission: OpenCodePermission): Promise<void> {
    await this.opencodeInteractions.handlePermissionUpdated(permission);
  }

  async handleOpenCodePermissionReplied(
    reply: OpenCodePermissionReplied,
  ): Promise<void> {
    await this.opencodeInteractions.handlePermissionReplied(reply);
  }

  async handleOpenCodeQuestionAsked(request: OpenCodeQuestionRequest): Promise<void> {
    await this.opencodeInteractions.handleQuestionAsked(request);
  }

  async handleOpenCodeQuestionReplied(reply: OpenCodeQuestionReplied): Promise<void> {
    await this.opencodeInteractions.handleQuestionReplied(reply);
  }

  async handleOpenCodeQuestionRejected(
    rejection: OpenCodeQuestionRejected,
  ): Promise<void> {
    await this.opencodeInteractions.handleQuestionRejected(rejection);
  }

  async handleOpenCodeMessagePartUpdated(
    properties: OpenCodeMessagePartUpdatedProperties,
  ): Promise<void> {
    await this.opencodeEvents.handleMessagePartUpdated(properties);
  }

  async handleOpenCodeMessageUpdated(message: OpenCodeMessage): Promise<void> {
    await this.opencodeEvents.handleMessageUpdated(message);
  }

  async handleSessionStartHook(
    payload: SessionStartHookPayload,
  ): Promise<Record<string, unknown>> {
    return await this.hookEvents.handleSessionStart(payload);
  }

  async handleSessionEndHook(
    payload: SessionEndHookPayload,
  ): Promise<Record<string, unknown>> {
    return await this.hookEvents.handleSessionEnd(payload);
  }

  async handlePermissionHook(
    payload: PermissionHookPayload,
    signal?: AbortSignal,
  ): Promise<Record<string, unknown>> {
    return await this.hookEvents.handlePermission(payload, signal);
  }

  async handleRequestUserInputHook(
    payload: RequestUserInputHookPayload,
  ): Promise<Record<string, unknown>> {
    return await this.hookEvents.handleRequestUserInput(payload);
  }

  async handleActivityHook(
    payload: ActivityHookPayload,
  ): Promise<Record<string, unknown>> {
    return await this.hookEvents.handleActivity(payload);
  }

  async handleStopHook(
    payload: StopHookPayload,
  ): Promise<Record<string, unknown>> {
    return await this.hookEvents.handleStop(payload);
  }

  private async handleCodexTranscriptError(
    event: CodexTranscriptErrorEvent,
  ): Promise<void> {
    const current = this.store.getSession(event.sessionId);
    if (
      !current ||
      current.status === "ended" ||
      (current.runtime !== undefined && current.runtime !== "codex") ||
      turnNotificationWasSent(current, event.turnId)
    ) {
      return;
    }
    const session = await this.store.upsertSession({
      sessionId: current.sessionId,
      cwd: current.cwd,
      model: current.model,
      turnId: event.turnId,
      status: "error",
      error: event.error,
      runtime: "codex",
      transcriptPath: event.transcriptPath,
    });
    await this.runtimeRetries.notifyTurnError(
      session,
      event.turnId,
      event.error,
      event.errorCode,
    );
  }

  private async recoverPendingTurnNotifications(): Promise<void> {
    for (const session of this.store.listPendingTurnNotifications()) {
      const turnId = session.lastNotificationTurnId;
      if (!turnId) continue;
      try {
        const kind = session.pendingNotificationKind ??
          (session.status === "error" ? "error" : "stop");
        const pendingMessage = session.pendingNotificationMessage;
        if (kind === "error") {
          const errorMessage = pendingMessage ?? session.lastError;
          if (!errorMessage) {
            await this.store.releaseTurnNotification(session.sessionId, turnId);
            continue;
          }
          await this.runtimeRetries.notifyTurnError(session, turnId, errorMessage);
          continue;
        }
        const fileDirectives = extractBridgeFileDirectives(
          pendingMessage?.trim() ||
            session.lastAssistantMessage?.trim() ||
            `${runtimeDisplayName(session.runtime)} 已结束本轮处理。`,
        );
        const message = fileDirectives.displayMessage ||
          `${runtimeDisplayName(session.runtime)} 已结束本轮处理。`;
        await this.turnNotifications.send(
          session,
          turnId,
          "stop",
          message,
          buildStopCards(session, message),
          "[stop] Failed to recover a pending Feishu completion card:",
        );
      } catch (error) {
        console.warn(
          `[notification] Could not recover pending turn ${turnId} for #${session.shortId}:`,
          error,
        );
      }
    }
  }


  private async watchCodexTranscript(session: SessionRecord): Promise<void> {
    if (
      session.status === "ended" ||
      (session.runtime !== undefined && session.runtime !== "codex") ||
      !session.transcriptPath
    ) {
      return;
    }
    await this.transcriptMonitor.watch(session.sessionId, session.transcriptPath);
  }

  async handleFeishuMessage(data: FeishuEvent): Promise<void> {
    await this.feishuMessages.handle(data);
  }

  async handleCardAction(data: FeishuEvent): Promise<CardActionResult> {
    return await this.cardActions.handle(data);
  }


  private async respond(
    sourceMessageId: string,
    chatId: string,
    text: string,
  ): Promise<string | undefined> {
    try {
      return await this.feishu.replyText(sourceMessageId, text);
    } catch (error) {
      console.warn("[message] Reply API failed; falling back to a new message.");
      try {
        return await this.feishu.sendText(chatId, text);
      } catch (fallbackError) {
        console.error("[message] Could not send Feishu response:", fallbackError ?? error);
        return undefined;
      }
    }
  }

  private async respondCard(
    _sourceMessageId: string,
    chatId: string,
    card: Record<string, unknown>,
  ): Promise<string | undefined> {
    try {
      return await this.feishu.sendCard(chatId, card);
    } catch (error) {
      console.error("[message] Could not send Feishu card response:", error);
      return undefined;
    }
  }

  private async addRoute(
    messageId: string,
    sessionId: string,
    chatId: string,
    kind: MessageRouteKind,
    requestId?: string,
  ): Promise<void> {
    await this.store.addMessageRoute({
      messageId,
      sessionId,
      requestId,
      chatId,
      kind,
      createdAt: new Date().toISOString(),
    });
  }
}
