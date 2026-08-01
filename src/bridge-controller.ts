import { randomUUID } from "node:crypto";
import path from "node:path";

import {
  appendAttachmentsToPrompt,
  LocalAttachmentStore,
  parseFeishuContent,
  type SavedAttachment,
} from "./attachments.js";
import {
  buildActivityCard,
  buildApprovalCard,
  buildErrorCards,
  buildResolvedApprovalCard,
  buildResolvedUserInputCard,
  buildStopCards,
  buildUserInputCard,
  buildUserPromptCards,
  type ActivityCardEvent,
} from "./cards.js";
import { readLastClaudeAssistantMessage } from "./claude-code-transcript.js";
import { readCodexTurnCompletion } from "./codex-transcript.js";
import type { CodexExitResult } from "./codex-runner.js";
import { CodexRunner } from "./codex-runner.js";
import {
  managedTerminalSessionId,
  ManagedTerminalRouter,
} from "./managed-terminal.js";
import { OpenCodeManager } from "./opencode-manager.js";
import type {
  OpenCodeMessage,
  OpenCodeMessagePartUpdatedProperties,
  OpenCodePermission,
  OpenCodeSession,
} from "./opencode-client.js";
import {
  captureLiveTrackedCodexProcessIds,
  type ClientProcessMetadata,
} from "./process-tracking.js";
import type {
  ActivityHookPayload,
  ApprovalRecord,
  ApprovalResolution,
  Binding,
  BridgeSettings,
  MessageRouteKind,
  PermissionHookPayload,
  RequestUserInputHookPayload,
  RuntimeName,
  SessionEndHookPayload,
  SessionRecord,
  SessionStartHookPayload,
  StopHookPayload,
  UserInputQuestion,
} from "./domain.js";
import {
  normalizeSessionAlias,
  previewJson,
  projectNameFromCwd,
  sessionAddress,
  sessionAliasKey,
  sessionAliasValidationError,
  sessionLabel,
  shortSessionId,
  statusLabel,
  stringifyModel,
  truncate,
  runtimeDefinition,
  runtimeDisplayName,
  runtimeGroupPrefix,
  runtimeReceivedText,
} from "./domain.js";
import { FeishuGateway } from "./feishu.js";
import {
  addFileReturnInstruction,
  extractBridgeFileDirectives,
  validateBridgeFile,
} from "./file-transfer.js";
import { BridgeStore } from "./store.js";

type FeishuEvent = Record<string, any>;

interface ApprovalWaiter {
  timer: NodeJS.Timeout;
  resolve: (resolution: ApprovalResolution) => void;
}

type UserInputResolution =
  | { kind: "answered"; answers: Record<string, string> }
  | { kind: "local" | "timeout" };

interface UserInputWaiter {
  sessionId: string;
  turnId: string;
  cwd: string;
  questions: UserInputQuestion[];
  messageIds: string[];
  timer: NodeJS.Timeout;
  resolve: (resolution: UserInputResolution) => void;
}

interface QueuedRemotePrompt {
  prompt: string;
  sourceMessageId: string;
  chatId: string;
  requestFileReturn: boolean;
  retryAttempt?: number;
}

interface PendingRuntimeLaunchPrompt extends QueuedRemotePrompt {
  queueRequested: boolean;
}

type RuntimeLaunchStatus = "pending" | "claimed" | "launched";

interface RuntimeLaunchRequest {
  requestId: string;
  sessionId: string;
  runtime: RuntimeName;
  cwd: string;
  elevated: boolean;
  createdAt: string;
  status: RuntimeLaunchStatus;
  timer: NodeJS.Timeout;
}

interface StagedAttachments {
  createdAt: number;
  files: SavedAttachment[];
}

interface PendingRemotePrompt {
  prompt: string;
  createdAt: number;
}

interface FileReturnRequest {
  chatId: string;
  remainingStops: number;
  expiresAt: number;
}

interface ActivityState {
  sessionId: string;
  turnId?: string;
  startedAt: string;
  events: ActivityCardEvent[];
  messageIds: Map<string, string>;
  lastSentAt: number;
  revision: number;
  sentRevision: number;
  completed: boolean;
  timer?: NodeJS.Timeout;
  flushing?: Promise<void>;
}

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
  uploadTtlMs: number;
  outboundFileMaxBytes: number;
  retryBaseDelayMs?: number;
  liveClientProcessIds?: (clients: ClientProcessMetadata[]) => ReadonlySet<number>;
}

interface ActionResult {
  toast: {
    type: "success" | "warning" | "error" | "info";
    content: string;
  };
}

interface AliasCommand {
  targetKind?: "short" | "alias";
  target?: string;
  alias?: string;
}

interface SessionAliasResult {
  ok: boolean;
  error?: string;
  session?: SessionRecord;
}

export class BridgeController {
  private readonly approvalWaiters = new Map<string, ApprovalWaiter>();
  private readonly inputWaiters = new Map<string, UserInputWaiter>();
  private readonly remoteInputLocks = new Set<string>();
  private readonly runtimeQueues = new Map<string, QueuedRemotePrompt[]>();
  private readonly managedQueueDepth = new Map<string, number>();
  private readonly pendingAttachments = new Map<string, StagedAttachments>();
  private readonly fileReturnRequests = new Map<string, FileReturnRequest[]>();
  private readonly activityStates = new Map<string, ActivityState>();
  private readonly pendingRemotePrompts = new Map<string, PendingRemotePrompt[]>();
  private readonly managedRetryCounts = new Map<string, number>();
  private readonly pendingRuntimeLaunchPrompts = new Map<
    string,
    PendingRuntimeLaunchPrompt[]
  >();
  private readonly runtimeLaunchRequests = new Map<string, RuntimeLaunchRequest>();
  private readonly runtimeLaunchRequestIds = new Map<string, string>();
  private readonly opencodePortSessions = new Map<number, Set<string>>();
  private readonly opencodeToolParts = new Map<string, Map<string, string>>();
  private readonly sessionGroupCreates = new Map<
    string,
    Promise<SessionRecord | undefined>
  >();
  private readonly attachmentStore: LocalAttachmentStore;

  constructor(
    private readonly store: BridgeStore,
    private readonly feishu: FeishuGateway,
    private readonly codex: CodexRunner,
    private readonly managedTerminals: ManagedTerminalRouter,
    private readonly opencode: OpenCodeManager | undefined,
    private readonly config: ControllerConfig,
  ) {
    this.attachmentStore = new LocalAttachmentStore(
      config.uploadsDirectory,
      config.inboundFileMaxBytes,
      config.inboundAttachmentMaxCount,
      config.uploadTtlMs,
    );
  }

  async initializeSessionGroups(): Promise<void> {
    if (!this.store.getOwnerOpenId()) {
      return;
    }
    const now = Date.now();
    const inactiveMs = this.config.sessionGroupInactiveMs ?? 7 * 24 * 60 * 60 * 1000;
    const sessions = this.store
      .listOpenSessions()
      .filter(
        (session) =>
          session.managedByAssistant === true &&
          (Boolean(session.feishuChatId) ||
            now - sessionGroupActivityTime(session) < inactiveMs),
      );
    for (const session of sessions) {
      await this.ensureSessionGroup(session.sessionId);
    }
  }

  async cleanupInactiveSessionGroups(
    now = Date.now(),
  ): Promise<{ deleted: number; failed: number }> {
    const inactiveMs = this.config.sessionGroupInactiveMs ?? 7 * 24 * 60 * 60 * 1000;
    let deleted = 0;
    let failed = 0;
    for (const session of this.store.listSessionsWithFeishuGroups()) {
      const chatId = session.feishuChatId;
      if (!chatId || now - sessionGroupActivityTime(session) < inactiveMs) {
        continue;
      }
      try {
        await this.feishu.deleteSessionGroup(chatId);
        await this.store.clearSessionFeishuChat(session.sessionId, chatId);
        deleted += 1;
        console.log(
          `[feishu] Dissolved inactive session group ${chatId} for #${session.shortId}.`,
        );
      } catch (error) {
        failed += 1;
        console.warn(
          `[feishu] Could not dissolve inactive group ${chatId} for #${session.shortId}:`,
          error,
        );
      }
    }
    return { deleted, failed };
  }

  handleRuntimeLaunchClaim(): Record<string, unknown> {
    const request = [...this.runtimeLaunchRequests.values()]
      .filter((item) => item.status === "pending")
      .sort(
        (left, right) =>
          left.createdAt.localeCompare(right.createdAt) ||
          left.requestId.localeCompare(right.requestId),
      )[0];
    if (!request) {
      return { ok: true };
    }
    request.status = "claimed";
    return {
      ok: true,
      request: {
        requestId: request.requestId,
        sessionId: request.sessionId,
        runtime: request.runtime,
        cwd: request.cwd,
        elevated: request.elevated,
        createdAt: request.createdAt,
      },
    };
  }

  async handleRuntimeLaunchComplete(
    value: Record<string, unknown>,
  ): Promise<Record<string, unknown>> {
    const requestId = typeof value.requestId === "string"
      ? value.requestId.trim()
      : "";
    const success = value.success;
    if (!requestId || typeof success !== "boolean") {
      return { ok: false, error: "自动恢复结果参数不完整。" };
    }
    const request = this.runtimeLaunchRequests.get(requestId);
    if (!request) {
      return { ok: true, alreadyResolved: true };
    }
    if (success) {
      request.status = "launched";
      return { ok: true, sessionId: request.sessionId };
    }
    const detail = typeof value.error === "string" && value.error.trim()
      ? truncate(value.error.trim(), 500)
      : "桌面助手未能启动对应窗口。";
    await this.failRuntimeLaunch(request, detail);
    return { ok: true, sessionId: request.sessionId };
  }

  health(): Record<string, unknown> {
    const sessions = this.listActiveSessions();
    const displayedSessions = [...sessions].sort(
      (left, right) =>
        left.openedAt.localeCompare(right.openedAt) ||
        left.sessionId.localeCompare(right.sessionId),
    );
    const activeSessionIds = new Set(sessions.map((session) => session.sessionId));
    const historySessions = this.store
      .listAssistantManagedSessions()
      .filter(
        (session) =>
          !activeSessionIds.has(session.sessionId) &&
          !this.managedTerminals.isOnline(session),
      );
    const approvals = this.listApprovalViews();
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
      queuedPrompts:
        (this.runtimeQueues.get(session.sessionId)?.length ?? 0) +
        (this.managedQueueDepth.get(session.sessionId) ?? 0),
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
      pendingApprovals: approvals.filter((approval) => approval.status === "pending").length,
      approvals,
      pendingInputs: this.inputWaiters.size,
      queuedPrompts:
        [...this.runtimeQueues.values()].reduce(
          (total, queue) => total + queue.length,
          0,
        ) +
        [...this.pendingRuntimeLaunchPrompts.values()].reduce(
          (total, queue) => total + queue.length,
          0,
        ) +
        [...this.managedQueueDepth.values()].reduce(
          (total, depth) => total + depth,
          0,
        ),
      runningResumes: sessions.filter((session) => this.codex.isRunning(session.sessionId))
        .length,
      pendingRuntimeLaunches: this.runtimeLaunchRequests.size,
      opencodeInstances: this.opencode?.listInstances().length ?? 0,
      activeSessionDefinition: this.activeSessionDefinition(),
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
      this.remoteInputLocks.delete(managedTerminalSessionId(terminalId));
    }
    if (session) {
      this.remoteInputLocks.delete(session.sessionId);
      this.runtimeQueues.delete(session.sessionId);
      this.managedQueueDepth.delete(session.sessionId);
      this.pendingRemotePrompts.delete(session.sessionId);
      this.fileReturnRequests.delete(session.sessionId);
      this.resolveInputsForSession(session.sessionId, "local");
      void this.finishActivity(session.sessionId, "窗口已关闭");
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
    const sessionId = typeof value.sessionId === "string" ? value.sessionId.trim() : "";
    const hasAlias = Object.prototype.hasOwnProperty.call(value, "alias");
    const aliasValue = value.alias;
    if (
      !sessionId ||
      !hasAlias ||
      (typeof aliasValue !== "string" && aliasValue !== null)
    ) {
      return { ok: false, error: "会话 ID 或别名参数不完整。" };
    }

    const session = this.listActiveSessions().find((item) => item.sessionId === sessionId);
    if (!session) {
      return { ok: false, error: "这个会话已不在活跃列表中，请刷新后重试。" };
    }

    const result = await this.updateSessionAlias(
      session,
      typeof aliasValue === "string" ? aliasValue : undefined,
    );
    return result.ok
      ? {
          ok: true,
          session: {
            sessionId: result.session?.sessionId,
            shortId: result.session?.shortId,
            alias: result.session?.alias ?? "",
          },
        }
      : { ok: false, error: result.error };
  }

  async handleSessionGroupRetry(
    value: Record<string, unknown>,
  ): Promise<Record<string, unknown>> {
    const sessionId = typeof value.sessionId === "string" ? value.sessionId.trim() : "";
    if (!sessionId || sessionId.length > 256) {
      return { ok: false, error: "会话 ID 参数不正确。" };
    }
    const session = this.store.getSession(sessionId);
    if (!session || session.managedByAssistant !== true) {
      return { ok: false, error: "这个会话不存在，或不是由助手创建的。" };
    }
    if (session.feishuChatId) {
      return {
        ok: true,
        alreadyConnected: true,
        chatId: session.feishuChatId,
        chatName: session.feishuChatName ?? "",
      };
    }
    await this.store.setSessionFeishuChatError(sessionId, undefined);
    const updated = await this.ensureSessionGroup(sessionId, true);
    return updated?.feishuChatId
      ? {
          ok: true,
          alreadyConnected: false,
          chatId: updated.feishuChatId,
          chatName: updated.feishuChatName ?? "",
        }
      : {
          ok: false,
          error: updated?.feishuChatError || "飞书群创建失败，请检查应用权限后重试。",
        };
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
    const requestId =
      typeof value.requestId === "string" ? value.requestId.trim() : "";
    const resolution = value.resolution;
    if (
      !requestId ||
      requestId.length > 128 ||
      (resolution !== "allow" &&
        resolution !== "deny")
    ) {
      return { ok: false, error: "审批请求或处理方式不正确。" };
    }

    const existing = this.store.getApproval(requestId);
    if (!existing) {
      return { ok: false, error: "审批请求不存在或已过期。" };
    }
    if (existing.status !== "pending") {
      return {
        ok: true,
        alreadyResolved: true,
        resolution: existing.resolution ?? "local",
        message: "这条审批已由另一端处理。",
      };
    }

    const completed = await this.completeApproval(requestId, resolution);
    if (!completed) {
      const current = this.store.getApproval(requestId);
      return current && current.status !== "pending"
        ? {
            ok: true,
            alreadyResolved: true,
            resolution: current.resolution ?? "local",
            message: "这条审批已由另一端处理。",
          }
        : { ok: false, error: "审批状态没有改变，请刷新后重试。" };
    }
    return {
      ok: true,
      alreadyResolved: false,
      resolution,
      message: approvalText(resolution, this.store.getSession(existing.sessionId)),
    };
  }

  async handleSettingsUpdate(
    value: Record<string, unknown>,
  ): Promise<Record<string, unknown>> {
    const booleanKeys = [
      "notifyActivity",
      "notifyUserPrompts",
      "autoRetryErrors",
      "autoApprove",
    ] as const;
    const update: Record<string, boolean | number> = {};
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
    const sessionId = session.id;
    const instance = this.opencode?.findInstanceBySession(sessionId);
    const cwd = session.directory || instance?.cwd || "";
    if (!cwd) {
      return;
    }
    const existing = this.store.getSession(sessionId);
    const record = await this.store.upsertSession({
      sessionId,
      cwd,
      model: session.model,
      status: existing?.status === "ended" ? "waiting" : existing?.status ?? "waiting",
      source: "opencode",
      runtime: "opencode",
      managedByAssistant: true,
    });
    const port = instance?.port;
    if (port !== undefined) {
      let sessionIds = this.opencodePortSessions.get(port);
      if (!sessionIds) {
        sessionIds = new Set();
        this.opencodePortSessions.set(port, sessionIds);
      }
      sessionIds.add(sessionId);
    }
    console.log(`[opencode] Registered session #${record.shortId} (${cwd}).`);
    if (!record.feishuChatId) {
      void this.ensureSessionGroup(sessionId).catch((error) => {
        console.warn("[opencode] Could not create Feishu group:", error);
      });
    }
    await this.tryDrainRuntimeLaunch(sessionId);
  }

  async handleOpenCodeSessionDeleted(sessionId: string): Promise<void> {
    await this.forgetOpenCodeSession(sessionId, "会话已关闭");
  }

  async handleOpenCodeSessionStatus(sessionId: string, status: string): Promise<void> {
    const current = this.store.getSession(sessionId);
    if (!current || current.runtime !== "opencode" || current.status === "ended") {
      return;
    }
    if (status === "busy") {
      await this.store.upsertSession({
        sessionId,
        cwd: current.cwd,
        model: current.model,
        status: "running",
        runtime: "opencode",
        managedByAssistant: true,
      });
    }
  }

  async handleOpenCodeSessionCompacted(sessionId: string): Promise<void> {
    const current = this.store.getSession(sessionId);
    if (!current || current.runtime !== "opencode") {
      return;
    }
    if (this.store.getSettings().notifyActivity) {
      await this.recordOpenCodeActivity(sessionId, {
        hook_event_name: "PreCompact",
        cwd: current.cwd,
      });
    }
  }

  async handleOpenCodeSessionIdle(sessionId: string): Promise<void> {
    this.remoteInputLocks.delete(sessionId);
    const current = this.store.getSession(sessionId);
    if (!current || current.runtime !== "opencode" || current.status === "ended") {
      return;
    }
    const result = await this.opencode?.lastAssistantText(sessionId);
    const assistantMessage = result?.text || undefined;
    const hasError = result?.hasError === true;
    const session = await this.store.upsertSession({
      sessionId,
      cwd: current.cwd,
      model: current.model,
      turnId: `opencode-${Date.now()}`,
      status: "waiting",
      assistantMessage,
      runtime: "opencode",
      managedByAssistant: true,
    });
    await this.finishActivity(sessionId, hasError ? "本轮发生错误" : "本轮处理完成");

    const fileDirectives = extractBridgeFileDirectives(
      assistantMessage?.trim() || "opencode 已结束本轮处理。",
    );
    const message =
      fileDirectives.displayMessage ||
      (hasError ? assistantMessage || "opencode 本轮发生错误。" : assistantMessage || "opencode 已结束本轮处理。");
    for (const recipient of await this.notificationRecipients(session)) {
      try {
        const cards = hasError
          ? buildErrorCards(session, message)
          : buildStopCards(session, message);
        for (const card of cards) {
          const messageId = await this.feishu.sendCard(recipient.chatId, card);
          await this.addRoute(messageId, sessionId, recipient.chatId, hasError ? "error" : "stop");
        }
      } catch (error) {
        console.error("[opencode] Failed to send a completion card:", error);
      }
    }
    await this.store.markStopNotified(sessionId, session.lastTurnId ?? "");

    const fileReturnRequest = this.advanceFileReturnRequests(sessionId);
    if (fileReturnRequest && fileDirectives.paths.length > 0) {
      void this.sendRequestedFiles(
        session,
        fileReturnRequest.chatId,
        fileDirectives.paths,
      ).catch((error) => {
        console.error("[files] Asynchronous file return failed:", error);
      });
    }
    void this.tryDrainOpenCodeQueue(sessionId);
  }

  async handleOpenCodeSessionError(
    sessionId: string,
    error: string | undefined,
  ): Promise<void> {
    const current = this.store.getSession(sessionId);
    if (!current || current.runtime !== "opencode" || current.status === "ended") {
      return;
    }
    const detail = truncate(error || "opencode 发生未知错误。", 500);
    const session = await this.store.upsertSession({
      sessionId,
      cwd: current.cwd,
      model: current.model,
      status: "error",
      error: detail,
      runtime: "opencode",
      managedByAssistant: true,
    });
    await this.finishActivity(sessionId, "本轮发生错误");
    for (const recipient of await this.notificationRecipients(session)) {
      try {
        for (const card of buildErrorCards(session, detail)) {
          const messageId = await this.feishu.sendCard(recipient.chatId, card);
          await this.addRoute(messageId, sessionId, recipient.chatId, "error");
        }
      } catch (sendError) {
        console.error("[opencode] Failed to send an error card:", sendError);
      }
    }
  }

  async handleOpenCodeInstanceDisconnected(port: number): Promise<void> {
    const sessionIds = this.opencodePortSessions.get(port);
    if (sessionIds) {
      for (const sessionId of [...sessionIds]) {
        await this.forgetOpenCodeSession(sessionId, "opencode 窗口已关闭");
      }
      this.opencodePortSessions.delete(port);
    }
  }

  async handleOpenCodePermissionUpdated(permission: OpenCodePermission): Promise<void> {
    const sessionId = permission.sessionID;
    if (!sessionId) {
      return;
    }
    const current = this.store.getSession(sessionId);
    const instance = this.opencode?.findInstanceBySession(sessionId);
    const cwd = current?.cwd || instance?.cwd || "";
    const session = await this.store.upsertSession({
      sessionId,
      cwd,
      model: current?.model,
      status: "pending_approval",
      runtime: "opencode",
      managedByAssistant: true,
    });
    const now = Date.now();
    const approval: ApprovalRecord = {
      requestId: randomUUID(),
      sessionId,
      turnId: current?.lastTurnId ?? `opencode-${now}`,
      cwd,
      toolName: permission.type ?? "permission",
      toolPreview: previewJson(permission.input ?? permission),
      createdAt: new Date(now).toISOString(),
      expiresAt: new Date(now + this.config.approvalTimeoutMs).toISOString(),
      status: "pending",
      messageIds: [],
      opencodePermissionId: permission.id,
    };
    await this.store.createApproval(approval);
    const timeoutTimer = setTimeout(() => {
      void this.completeApproval(approval.requestId, "timeout");
    }, this.config.approvalTimeoutMs);
    timeoutTimer.unref?.();

    for (const recipient of await this.notificationRecipients(session)) {
      try {
        const messageId = await this.feishu.sendCard(
          recipient.chatId,
          buildApprovalCard(session, approval),
        );
        await this.store.addApprovalMessage(approval.requestId, messageId);
        await this.addRoute(messageId, sessionId, recipient.chatId, "approval", approval.requestId);
      } catch (error) {
        console.error("[opencode] Failed to send an approval card:", error);
      }
    }
    if (this.store.getSettings().autoApprove) {
      await this.completeApproval(approval.requestId, "allow");
      console.log(`[opencode] Auto-approved permission for #${session.shortId}.`);
    }
  }

  async handleOpenCodeMessagePartUpdated(
    properties: OpenCodeMessagePartUpdatedProperties,
  ): Promise<void> {
    const sessionId = properties.sessionID;
    const part = properties.part;
    if (!sessionId || !part || !this.store.getSettings().notifyActivity) {
      return;
    }
    const status = part.state?.status;
    if (!status || part.type !== "tool") {
      return;
    }
    const current = this.store.getSession(sessionId);
    if (!current || current.runtime !== "opencode") {
      return;
    }
    let partsBySession = this.opencodeToolParts.get(sessionId);
    if (!partsBySession) {
      partsBySession = new Map();
      this.opencodeToolParts.set(sessionId, partsBySession);
    }
    const partId = part.id || `${properties.messageID ?? "?"}-${part.tool}-${part.state?.title ?? ""}`;
    const previous = partsBySession.get(partId);
    if (previous === status) {
      return;
    }
    partsBySession.set(partId, status);
    const toolName = part.tool;
    if (status === "running" || status === "pending") {
      await this.recordOpenCodeActivity(sessionId, {
        hook_event_name: "PreToolUse",
        cwd: current.cwd,
        tool_name: toolName,
        tool_preview: previewJson(part.state?.input, 800),
      });
    } else if (status === "completed") {
      await this.recordOpenCodeActivity(sessionId, {
        hook_event_name: "PostToolUse",
        cwd: current.cwd,
        tool_name: toolName,
        tool_response_preview: previewJson(part.state?.output, 800),
      });
    }
  }

  async handleOpenCodeMessageUpdated(message: OpenCodeMessage): Promise<void> {
    if (message.role !== "user" || !message.sessionID) {
      return;
    }
    const prompt = (message.parts ?? [])
      .filter((part) => part.type === "text" && typeof part.text === "string")
      .map((part) => part.text as string)
      .join("\n")
      .trim();
    if (!prompt) {
      return;
    }
    const sessionId = message.sessionID;
    if (this.consumeRemotePrompt(sessionId, prompt)) {
      return;
    }
    const settings = this.store.getSettings();
    const session = this.store.getSession(sessionId);
    if (!settings.notifyUserPrompts || session?.managedByAssistant !== true) {
      return;
    }
    await this.store.upsertSession({
      sessionId,
      cwd: session.cwd,
      model: session.model,
      status: "running",
      runtime: "opencode",
      managedByAssistant: true,
    });
    for (const recipient of await this.notificationRecipients(session)) {
      try {
        for (const card of buildUserPromptCards(session, prompt)) {
          const messageId = await this.feishu.sendCard(recipient.chatId, card);
          await this.addRoute(messageId, sessionId, recipient.chatId, "user_prompt");
        }
      } catch (error) {
        console.error("[opencode] Failed to send a PC prompt card:", error);
      }
    }
  }

  async handleSessionStartHook(
    payload: SessionStartHookPayload,
  ): Promise<Record<string, unknown>> {
    const claimedTerminal = payload.managed_terminal_id
      ? this.managedTerminals.claimById(
          payload.managed_terminal_id,
          payload.cwd,
          payload.session_id,
        )
      : this.managedTerminals.claim(payload.cwd, payload.session_id);
    const managedTerminalId =
      payload.managed_terminal_id ?? claimedTerminal?.terminalId ?? null;
    const managedTerminalElevated =
      managedTerminalId
        ? payload.managed_terminal_elevated ?? claimedTerminal?.elevated ?? null
        : null;
    const placeholder = managedTerminalId
      ? this.store.findSessionByManagedTerminalId(managedTerminalId)
      : undefined;
    const openedAt =
      placeholder?.openedAt ??
      (claimedTerminal
        ? new Date(claimedTerminal.createdAt).toISOString()
        : undefined);
    const session = await this.store.upsertSession({
      sessionId: payload.session_id,
      alias: placeholder?.source === "managed_window" ? placeholder.alias : undefined,
      cwd: payload.cwd,
      model: payload.model,
      status: managedTerminalId ? "waiting" : "running",
      source: payload.source,
      runtime: payload.runtime ?? claimedTerminal?.runtime,
      clientProcessId: managedTerminalId
        ? null
        : payload.client_process_id ?? null,
      clientProcessStartedAt: managedTerminalId
        ? null
        : payload.client_process_started_at ?? null,
      managedTerminalId,
      managedTerminalElevated,
      managedByAssistant: managedTerminalId ? true : undefined,
      openedAt,
    });
    if (
      placeholder?.source === "managed_window" &&
      placeholder.sessionId !== session.sessionId
    ) {
      if (this.remoteInputLocks.delete(placeholder.sessionId)) {
        this.remoteInputLocks.add(session.sessionId);
      }
      const queueDepth = this.managedQueueDepth.get(placeholder.sessionId);
      if (queueDepth !== undefined) {
        this.managedQueueDepth.delete(placeholder.sessionId);
        this.managedQueueDepth.set(session.sessionId, queueDepth);
      }
      const fileRequests = this.fileReturnRequests.get(placeholder.sessionId);
      if (fileRequests) {
        this.fileReturnRequests.delete(placeholder.sessionId);
        this.fileReturnRequests.set(session.sessionId, fileRequests);
      }
      const pendingPrompts = this.pendingRemotePrompts.get(placeholder.sessionId);
      if (pendingPrompts) {
        this.pendingRemotePrompts.delete(placeholder.sessionId);
        this.pendingRemotePrompts.set(session.sessionId, pendingPrompts);
      }
      const activity = this.activityStates.get(placeholder.sessionId);
      if (activity) {
        this.activityStates.delete(placeholder.sessionId);
        activity.sessionId = session.sessionId;
        this.activityStates.set(session.sessionId, activity);
      }
      await this.store.replaceSessionReferences(placeholder.sessionId, session.sessionId);
    }
    const currentSession = this.store.getSession(session.sessionId) ?? session;
    console.log(
      `[session] ${payload.source} registered session #${currentSession.shortId}.`,
    );
    if (currentSession.managedByAssistant === true && !currentSession.feishuChatId) {
      void this.ensureSessionGroup(currentSession.sessionId);
    }
    await this.tryDrainRuntimeLaunch(currentSession.sessionId);
    return {};
  }

  async handleSessionEndHook(
    payload: SessionEndHookPayload,
  ): Promise<Record<string, unknown>> {
    const session = await this.store.upsertSession({
      sessionId: payload.session_id,
      cwd: payload.cwd,
      status: "ended",
      ...(payload.managed_terminal_id !== undefined
        ? { managedTerminalId: payload.managed_terminal_id }
        : {}),
      ...(payload.managed_terminal_elevated !== undefined
        ? { managedTerminalElevated: payload.managed_terminal_elevated }
        : {}),
    });
    this.remoteInputLocks.delete(payload.session_id);
    this.runtimeQueues.delete(payload.session_id);
    this.managedQueueDepth.delete(payload.session_id);
    this.pendingRemotePrompts.delete(payload.session_id);
    this.fileReturnRequests.delete(payload.session_id);
    this.resolveInputsForSession(payload.session_id, "local");
    void this.finishActivity(payload.session_id, "会话已结束");
    this.managedTerminals.release(payload.session_id);
    console.log(`[session] Ended session #${session.shortId}.`);
    return {};
  }

  async handlePermissionHook(
    payload: PermissionHookPayload,
  ): Promise<Record<string, unknown>> {
    const session = await this.store.upsertSession({
      sessionId: payload.session_id,
      cwd: payload.cwd,
      model: payload.model,
      turnId: payload.turn_id,
      status: "pending_approval",
      runtime: payload.runtime,
      ...(payload.managed_terminal_id !== undefined
        ? { managedTerminalId: payload.managed_terminal_id }
        : {}),
      ...(payload.managed_terminal_elevated !== undefined
        ? { managedTerminalElevated: payload.managed_terminal_elevated }
        : {}),
    });

    const now = Date.now();
    const approval: ApprovalRecord = {
      requestId: randomUUID(),
      sessionId: payload.session_id,
      turnId: payload.turn_id,
      cwd: payload.cwd,
      toolName: payload.tool_name,
      toolPreview: previewJson(payload.tool_input),
      createdAt: new Date(now).toISOString(),
      expiresAt: new Date(now + this.config.approvalTimeoutMs).toISOString(),
      status: "pending",
      messageIds: [],
    };
    await this.store.createApproval(approval);

    const recipients = await this.notificationRecipients(session);
    const resultPromise = new Promise<ApprovalResolution>((resolve) => {
      const timer = setTimeout(() => {
        void this.completeApproval(approval.requestId, "timeout");
      }, this.config.approvalTimeoutMs);
      this.approvalWaiters.set(approval.requestId, { timer, resolve });
    });

    let sentCount = 0;
    for (const recipient of recipients) {
      try {
        const messageId = await this.feishu.sendCard(
          recipient.chatId,
          buildApprovalCard(session, approval),
        );
        sentCount += 1;
        await this.store.addApprovalMessage(approval.requestId, messageId);
        await this.addRoute(
          messageId,
          payload.session_id,
          recipient.chatId,
          "approval",
          approval.requestId,
        );
      } catch (error) {
        console.error("[approval] Failed to send a Feishu approval card:", error);
      }
    }

    const autoApprove = this.store.getSettings().autoApprove;
    if (autoApprove) {
      await this.completeApproval(approval.requestId, "allow");
      console.log(`[approval] Auto-approved for session #${session.shortId}.`);
    }

    if (!autoApprove) {
      if (sentCount > 0) {
        console.log(
          `[approval] Waiting for desktop or Feishu decision for session #${session.shortId}.`,
        );
      } else {
        console.warn(
          `[approval] Feishu unavailable; waiting for desktop decision for session #${session.shortId}.`,
        );
      }
    }

    const resolution = await resultPromise;
    if (resolution === "allow") {
      return {
        hookSpecificOutput: {
          hookEventName: "PermissionRequest",
          decision: { behavior: "allow" },
        },
      };
    }
    if (resolution === "deny") {
      return {
        hookSpecificOutput: {
          hookEventName: "PermissionRequest",
          decision: {
            behavior: "deny",
            message: "用户已通过飞书拒绝这次操作。",
          },
        },
      };
    }
    return {};
  }

  async handleRequestUserInputHook(
    payload: RequestUserInputHookPayload,
  ): Promise<Record<string, unknown>> {
    const session = await this.store.upsertSession({
      sessionId: payload.session_id,
      cwd: payload.cwd,
      model: payload.model,
      turnId: payload.turn_id,
      status: "pending_input",
      runtime: payload.runtime,
      ...(payload.managed_terminal_id !== undefined
        ? { managedTerminalId: payload.managed_terminal_id }
        : {}),
      ...(payload.managed_terminal_elevated !== undefined
        ? { managedTerminalElevated: payload.managed_terminal_elevated }
        : {}),
    });
    const recipients = await this.notificationRecipients(session);
    if (recipients.length === 0) {
      return {};
    }

    const requestId = randomUUID();
    const autoResolutionMs = payload.tool_input.autoResolutionMs;
    const timeoutMs = typeof autoResolutionMs === "number" && autoResolutionMs > 0
      ? Math.min(this.config.inputTimeoutMs, autoResolutionMs)
      : this.config.inputTimeoutMs;
    const resultPromise = new Promise<UserInputResolution>((resolve) => {
      const timer = setTimeout(() => {
        void this.completeUserInput(requestId, { kind: "timeout" });
      }, timeoutMs);
      this.inputWaiters.set(requestId, {
        sessionId: payload.session_id,
        turnId: payload.turn_id,
        cwd: payload.cwd,
        questions: payload.tool_input.questions,
        messageIds: [],
        timer,
        resolve,
      });
    });

    let sentCount = 0;
    for (const recipient of recipients) {
      try {
        const messageId = await this.feishu.sendCard(
          recipient.chatId,
          buildUserInputCard(session, requestId, payload.tool_input.questions),
        );
        sentCount += 1;
        this.inputWaiters.get(requestId)?.messageIds.push(messageId);
        await this.addRoute(
          messageId,
          payload.session_id,
          recipient.chatId,
          "input",
          requestId,
        );
      } catch (error) {
        console.error("[input] Failed to send a Feishu question card:", error);
      }
    }
    if (sentCount === 0) {
      await this.completeUserInput(requestId, { kind: "local" });
    }

    const resolution = await resultPromise;
    if (resolution.kind !== "answered") {
      return {};
    }
    const answerText = payload.tool_input.questions
      .map(
        (question, index) =>
          `${index + 1}. ${question.header} (${question.id}): ${resolution.answers[question.id] ?? ""}`,
      )
      .join("\n");
    if (payload.runtime === "claudecode") {
      const originalInput = payload.tool_input.claudeCodeOriginalInput;
      const questionTextById = payload.tool_input.claudeCodeQuestionTextById;
      if (originalInput && questionTextById) {
        const answers = Object.fromEntries(
          payload.tool_input.questions.flatMap((question) => {
            const questionText = questionTextById[question.id];
            return questionText
              ? [[questionText, resolution.answers[question.id] ?? ""]]
              : [];
          }),
        );
        return {
          hookSpecificOutput: {
            hookEventName: "PreToolUse",
            permissionDecision: "allow",
            updatedInput: {
              ...originalInput,
              answers,
              annotations: {},
            },
          },
        };
      }
    }
    return {
      hookSpecificOutput: {
        hookEventName: "PreToolUse",
        permissionDecision: "deny",
        permissionDecisionReason: `request_user_input 已由用户通过飞书回答：\n${answerText}\n请直接使用这些答案继续，不要再次询问同一组问题。`,
      },
    };
  }

  async handleActivityHook(
    payload: ActivityHookPayload,
  ): Promise<Record<string, unknown>> {
    const settings = this.store.getSettings();
    if (payload.hook_event_name === "UserPromptSubmit") {
      void this.handleUserPromptSubmit(payload, settings).catch((error) => {
        console.error("[prompt] Could not sync the PC prompt to Feishu:", error);
      });
    }
    if (settings.notifyActivity) {
      void this.recordActivity(payload).catch((error) => {
        console.error("[activity] Could not record Codex activity:", error);
      });
    }
    return {};
  }

  async handleStopHook(payload: StopHookPayload): Promise<Record<string, unknown>> {
    if ((this.managedQueueDepth.get(payload.session_id) ?? 0) > 0) {
      this.remoteInputLocks.add(payload.session_id);
    } else {
      this.remoteInputLocks.delete(payload.session_id);
    }
    const previous = this.store.getSession(payload.session_id);

    let assistantMessage = payload.last_assistant_message;
    let turnId = payload.turn_id;
    let structuredCodexError: string | undefined;
    let structuredCodexErrorCode: string | undefined;
    if (payload.runtime === "claudecode" && payload.transcript_path) {
      const transcriptMessage = await readLastClaudeAssistantMessage(payload.transcript_path);
      assistantMessage ||= transcriptMessage?.text ?? null;
      turnId = transcriptMessage?.turnId ?? turnId;
    } else if (payload.transcript_path) {
      const completion = await readCodexTurnCompletion(payload.transcript_path, turnId);
      assistantMessage ||= completion?.assistantMessage ?? null;
      structuredCodexError = completion?.error;
      structuredCodexErrorCode = completion?.errorCode;
      turnId = completion?.turnId ?? turnId;
    }

    const session = await this.store.upsertSession({
      sessionId: payload.session_id,
      cwd: payload.cwd,
      model: payload.model,
      turnId,
      status: "waiting",
      assistantMessage,
      runtime: payload.runtime,
      ...(payload.managed_terminal_id !== undefined
        ? { managedTerminalId: payload.managed_terminal_id }
        : {}),
      ...(payload.managed_terminal_elevated !== undefined
        ? { managedTerminalElevated: payload.managed_terminal_elevated }
        : {}),
    });
    const codexError = structuredCodexError ?? codexErrorFromMessage(assistantMessage);
    await this.finishActivity(
      payload.session_id,
      codexError ? "本轮发生错误" : "本轮处理完成",
    );

    if (previous?.lastNotificationTurnId === turnId) {
      return {};
    }
    if (codexError) {
      const retryCount = this.managedRetryCounts.get(payload.session_id) ?? 0;
      const retrySettings = this.store.getSettings();
      const retryDelay = retryDelayMs(retrySettings, this.config.retryBaseDelayMs);
      const canRetry =
        retrySettings.autoRetryErrors &&
        retryCount < retrySettings.retryMaxAttempts &&
        isRetryableCodexError(codexError, structuredCodexErrorCode) &&
        this.managedTerminals.isReady(session);
      const detail = canRetry
        ? `${codexError}\n\n助手将在 ${Math.ceil(retryDelay / 1_000)} 秒后自动重试（第 ${retryCount + 1}/${retrySettings.retryMaxAttempts} 次）。`
        : codexError;
      const failedSession = await this.store.upsertSession({
        sessionId: session.sessionId,
        cwd: session.cwd,
        model: session.model,
        status: "error",
        error: codexError,
      });
      for (const recipient of await this.notificationRecipients(failedSession)) {
        try {
          for (const card of buildErrorCards(failedSession, detail)) {
            const messageId = await this.feishu.sendCard(recipient.chatId, card);
            await this.addRoute(messageId, payload.session_id, recipient.chatId, "error");
          }
        } catch (error) {
          console.error("[stop] Failed to send a Codex error card:", error);
        }
      }
      await this.store.markStopNotified(payload.session_id, turnId);
      if (canRetry) {
        this.managedRetryCounts.set(payload.session_id, retryCount + 1);
        setTimeout(() => {
          const current = this.store.getSession(payload.session_id);
          const currentSettings = this.store.getSettings();
          if (
            !currentSettings.autoRetryErrors ||
            retryCount >= currentSettings.retryMaxAttempts ||
            !current ||
            !this.managedTerminals.isReady(current)
          ) {
            return;
          }
          const retryPrompt =
            "刚才的请求因临时服务错误失败。请重试上一项任务，并继续从中断处执行。";
          this.rememberRemotePrompt(payload.session_id, retryPrompt);
          void this.managedTerminals.send(
            current,
            retryPrompt,
            "steer",
          ).catch((error) => {
            this.forgetRemotePrompt(payload.session_id, retryPrompt);
            console.error("[retry] Managed retry failed:", error);
          });
        }, retryDelay);
      }
      return {};
    }
    this.managedRetryCounts.delete(payload.session_id);
    const fileReturnRequest = this.advanceFileReturnRequests(payload.session_id);
    this.decrementManagedQueueDepth(payload.session_id);

    const fileDirectives = extractBridgeFileDirectives(
      assistantMessage?.trim() || `${runtimeDisplayName(session.runtime)} 已结束本轮处理。`,
    );
    const message = fileDirectives.displayMessage ||
      `${runtimeDisplayName(session.runtime)} 已结束本轮处理。`;
    let sentCount = 0;
    for (const recipient of await this.notificationRecipients(session)) {
      try {
        for (const card of buildStopCards(session, message)) {
          const messageId = await this.feishu.sendCard(recipient.chatId, card);
          sentCount += 1;
          await this.addRoute(messageId, payload.session_id, recipient.chatId, "stop");
        }
      } catch (error) {
        console.error("[stop] Failed to send a Feishu completion card:", error);
      }
    }
    if (sentCount > 0) {
      await this.store.markStopNotified(payload.session_id, turnId);
      console.log(`[stop] Notified Feishu for session #${session.shortId}.`);
    }
    if (fileReturnRequest && fileDirectives.paths.length > 0) {
      void this.sendRequestedFiles(
        session,
        fileReturnRequest.chatId,
        fileDirectives.paths,
      ).catch((error) => {
        console.error("[files] Asynchronous file return failed:", error);
      });
    }
    void this.tryDrainExternalQueue(payload.session_id);
    return {};
  }

  async handleFeishuMessage(data: FeishuEvent): Promise<void> {
    const openId = data.sender?.sender_id?.open_id;
    const message = data.message;
    const chatId = message?.chat_id;
    const messageId = message?.message_id;
    const chatType = message?.chat_type ?? "unknown";
    const parsedContent = parseFeishuContent(message);
    const text = parsedContent.text;

    if (!openId || !chatId || !messageId) {
      console.warn("[message] Ignored a message without sender, chat, or message id.");
      return;
    }

    if (!(await this.store.claimInboundMessage(messageId))) {
      console.log(`[message] Ignored duplicate Feishu message ${messageId}.`);
      return;
    }

    console.log(
      `[message] Received Feishu ${String(message?.message_type ?? "text")} (${text.length} chars, ${parsedContent.attachments.length} attachments, ${chatType}).`,
    );

    const bindAttempt = chatType === "p2p"
      ? parseBindCommand(text, this.config.bindCommand)
      : { matched: false };
    if (bindAttempt.matched) {
      const result = await this.store.bindOwner(
        {
          openId,
          chatId,
          chatType,
          boundAt: new Date().toISOString(),
        },
        bindAttempt.code,
      );
      if (result === "invalid_code") {
        await this.respond(
          messageId,
          chatId,
          `绑定码不正确。请在电脑端 Codex 飞书助手中查看本机绑定命令，再发送“${this.config.bindCommand} 绑定码”。`,
        );
        return;
      }
      if (result === "owner_mismatch") {
        await this.respond(
          messageId,
          chatId,
          "这个助手已经设置了唯一管理员，其他飞书账号不能绑定或控制本机 Codex。",
        );
        return;
      }
      await this.respond(
        messageId,
        chatId,
        result === "bound"
          ? "绑定成功，你已成为这台电脑上 Codex 助手的唯一管理员。"
          : "管理员绑定已恢复。现在可以继续接收通知和回复 Codex。",
      );
      void this.initializeSessionGroups().catch((error) => {
        console.warn("[feishu] Could not initialize existing session groups:", error);
      });
      return;
    }

    if (chatType === "p2p" && text === "解绑") {
      const removed = await this.store.removeBinding(openId);
      await this.respond(messageId, chatId, removed ? "已解绑。" : "当前账号还没有绑定。");
      return;
    }

    if (!this.store.isBound(openId)) {
      await this.respond(
        messageId,
        chatId,
        this.store.getOwnerOpenId()
          ? "飞书连接正常，但这个助手只允许已设置的管理员账号操作。"
          : `飞书连接正常。请先在电脑端查看随机绑定码，然后私聊发送“${this.config.bindCommand} 绑定码”。`,
      );
      return;
    }

    const groupSession = chatType === "p2p"
      ? undefined
      : this.store.findSessionByFeishuChatId(chatId);
    if (chatType !== "p2p" && !groupSession) {
      await this.respond(
        messageId,
        chatId,
        codexNotReceived("当前群未绑定会话。"),
      );
      return;
    }
    if (groupSession) {
      await this.store.touchSessionActivity(groupSession.sessionId);
    }

    const attachmentKey = this.attachmentKey(openId, chatId);
    if (parsedContent.attachments.length > 0) {
      try {
        const downloaded = await this.attachmentStore.download(
          this.feishu,
          messageId,
          parsedContent.attachments,
        );
        this.stageAttachments(attachmentKey, downloaded);
      } catch (error) {
        const detail = error instanceof Error ? error.message : String(error);
        await this.respond(messageId, chatId, `附件接收失败：${truncate(detail, 500)}`);
        return;
      }
      if (!text) {
        const staged = this.peekAttachments(attachmentKey);
        await this.respond(
          messageId,
          chatId,
          groupSession
            ? `已安全保存 ${parsedContent.attachments.length} 个附件（当前暂存 ${staged.length} 个）。下一条直接发送处理要求即可。`
            : `已安全保存 ${parsedContent.attachments.length} 个附件（当前暂存 ${staged.length} 个）。下一条请发送处理要求；有多个窗口时请写成“@别名 要求”或“#短ID 要求”。`,
        );
        return;
      }
    }

    // Session groups are a direct Codex input surface. Keep bridge
    // administration in the bot's private chat so words such as “状态” or
    // “帮助” can still be sent to the corresponding Codex session.
    const isPrivateChat = chatType === "p2p";

    if (isPrivateChat && text === "状态") {
      const sessions = this.listActiveSessions();
      const pending = sessions.filter((session) => session.status === "pending_approval").length;
      const queued = [...this.runtimeQueues.values()].reduce(
        (total, queue) => total + queue.length,
        0,
      ) + [...this.managedQueueDepth.values()].reduce(
        (total, depth) => total + depth,
        0,
      );
      await this.respond(
        messageId,
        chatId,
        `飞书桥接在线，当前账号已绑定。活跃会话 ${sessions.length} 个，待审批 ${pending} 个，待补充 ${this.inputWaiters.size} 个，排队 ${queued} 条。\n${this.activeSessionDefinition()}`,
      );
      return;
    }

    if (isPrivateChat && (text === "会话" || text.toLowerCase() === "sessions")) {
      await this.respond(messageId, chatId, this.formatSessionList());
      return;
    }

    if (isPrivateChat) {
      const aliasCommand = parseAliasCommand(text);
      if (aliasCommand) {
        await this.handleFeishuAliasCommand(aliasCommand, messageId, chatId);
        return;
      }
      if (/^别名(?:\s|$)/.test(text)) {
        await this.respond(messageId, chatId, aliasCommandUsage());
        return;
      }
    }

    if (isPrivateChat && text === "帮助") {
      await this.respond(
        messageId,
        chatId,
        "用法：\n1. 引用助手同步窗口的 Codex 通知并回复；\n2. 发送 @别名 内容，运行中会直接插话；\n3. 发送“排队 @别名 内容”，排到下一轮；\n4. 发送“发文件 @别名 要求”，让 Codex 完成后把文件发回；\n5. 可直接发送图片或文件，下一条再发送处理要求；\n6. 发送“会话”或“别名”查看路由；\n7. 外部会话仅支持通知、审批和补充信息，普通消息不会写入；\n8. 审批和补充信息卡片都可在飞书处理。",
      );
      return;
    }

    if (!text) {
      await this.respond(messageId, chatId, "没有识别到文字或可下载的附件。请发送“帮助”查看用法。");
      return;
    }

    const quotedRoute = this.store.findMessageRoute([
      message?.parent_id,
      message?.root_id,
    ]);

    if (quotedRoute?.kind === "input" && quotedRoute.requestId) {
      const waiter = this.inputWaiters.get(quotedRoute.requestId);
      if (!waiter) {
        await this.respond(messageId, chatId, "这组问题已经处理或失效。");
        return;
      }
      const answers = parseUserInputAnswers(text, waiter.questions);
      if (!answers) {
        await this.respond(
          messageId,
          chatId,
          inputAnswerUsage(waiter.questions),
        );
        return;
      }
      const completed = await this.completeUserInput(quotedRoute.requestId, {
        kind: "answered",
        answers,
      });
      const inputSession = this.store.getSession(waiter.sessionId);
      await this.respond(
        messageId,
        chatId,
        completed
          ? receivedText(inputSession)
          : notReceivedText(inputSession, "问题已处理或失效。"),
      );
      return;
    }

    if (quotedRoute?.requestId && this.store.hasPendingApprovalForSession(quotedRoute.sessionId)) {
      const approvalResolution = approvalResolutionFromText(text);
      if (approvalResolution) {
        const approvalSession = this.store.getSession(quotedRoute.sessionId);
        const completed = await this.completeApproval(
          quotedRoute.requestId,
          approvalResolution,
        );
        await this.respond(
          messageId,
          chatId,
          completed
            ? approvalText(approvalResolution, approvalSession)
            : "这条审批已经处理或失效。",
        );
      } else {
        await this.respond(
          messageId,
          chatId,
          "这个会话正在等待审批。请点击审批卡片按钮，或引用卡片回复“批准”或“拒绝”。",
        );
      }
      return;
    }

    const leadingDirectives = parsePromptDirectives(text);
    const explicit = parseExplicitSession(leadingDirectives.prompt) ??
      parseExplicitAlias(leadingDirectives.prompt);

    let target: SessionRecord | undefined;
    let prompt = leadingDirectives.prompt;
    let queueRequested = leadingDirectives.queue;
    let fileReturnRequested = leadingDirectives.fileReturn;

    if (groupSession) {
      target = this.store.getSession(groupSession.sessionId) ?? groupSession;
      // A session group is already an unambiguous route. Ignore @alias/#id
      // prefixes here so one group can never accidentally steer another session.
      if (explicit) {
        prompt = explicit.prompt;
      }
    } else if (explicit) {
      const matches = explicit.kind === "short"
        ? this.findActiveSessionsByShortToken(explicit.token)
        : this.findActiveSessionsByAlias(explicit.token);
      const address = explicit.kind === "short"
        ? `#${explicit.token}`
        : `@${explicit.token}`;
      if (matches.length !== 1) {
        await this.respond(
          messageId,
          chatId,
          matches.length === 0
            ? codexNotReceived(`没有找到 ${address} 对应的活跃会话。`)
            : explicit.kind === "short"
              ? codexNotReceived(`${address} 匹配到多个会话。`)
              : codexNotReceived(`${address} 不是唯一别名。`),
        );
        return;
      }
      target = matches[0];
      prompt = explicit.prompt;
    } else if (quotedRoute) {
      target = this.listActiveSessions().find(
        (session) => session.sessionId === quotedRoute.sessionId,
      );
    } else {
      const activeSessions = this.listActiveSessions();
      if (activeSessions.length === 1) {
        target = activeSessions[0];
      } else {
        await this.respond(
          messageId,
          chatId,
          activeSessions.length === 0
            ? codexNotReceived("当前没有活跃会话。")
            : codexNotReceived("有多个活跃会话，请指定目标。"),
        );
        return;
      }
    }

    if (!target) {
      await this.respond(
        messageId,
        chatId,
        groupSession
          ? codexNotReceived("对应窗口已关闭。")
          : codexNotReceived("对应会话不可用。"),
      );
      return;
    }
    const nestedDirectives = parsePromptDirectives(prompt);
    prompt = nestedDirectives.prompt;
    queueRequested ||= nestedDirectives.queue;
    fileReturnRequested ||= nestedDirectives.fileReturn;
    if (!prompt) {
      await this.respond(messageId, chatId, codexNotReceived("内容为空。"));
      return;
    }
    const attachments = this.takeAttachments(attachmentKey);
    prompt = appendAttachmentsToPrompt(prompt, attachments);
    if (fileReturnRequested) {
      prompt = addFileReturnInstruction(prompt);
    }
    const targetRuntime = runtimeDefinition(target.runtime);
    if (
      groupSession &&
      target.managedByAssistant === true &&
      !this.isRuntimeAvailable(target)
    ) {
      await this.queueRuntimeLaunch(target, {
        prompt,
        sourceMessageId: messageId,
        chatId,
        requestFileReturn: fileReturnRequested,
        queueRequested,
      });
      return;
    }
    if (
      !this.managedTerminals.isManaged(target) &&
      targetRuntime.transport !== "http_event_stream"
    ) {
      await this.respond(
        messageId,
        chatId,
        externalSessionInputBlockedMessage(target),
      );
      return;
    }
    if (
      targetRuntime.transport === "http_event_stream" &&
      !this.opencode?.findInstanceBySession(target.sessionId)
    ) {
      await this.respond(
        messageId,
        chatId,
        notReceivedText(target, "opencode 窗口未连接。"),
      );
      return;
    }
    await this.resumeSession(
      target,
      prompt,
      messageId,
      chatId,
      queueRequested,
      fileReturnRequested,
    );
  }

  async handleCardAction(data: FeishuEvent): Promise<ActionResult> {
    const actionValue = normalizeActionValue(data.action?.value);
    const action = actionValue?.action;
    const requestId = actionValue?.requestId;
    const operatorOpenId = data.operator?.open_id;

    if (!operatorOpenId || !this.store.isBound(operatorOpenId)) {
      return { toast: { type: "warning", content: "只有已绑定的管理员可以审批。" } };
    }
    if (typeof requestId !== "string") {
      return { toast: { type: "error", content: "审批参数不完整。" } };
    }

    if (action === "input_answer" || action === "input_local") {
      const waiter = this.inputWaiters.get(requestId);
      if (
        !waiter ||
        (typeof actionValue?.sessionId === "string" &&
          waiter.sessionId !== actionValue.sessionId)
      ) {
        return { toast: { type: "warning", content: "这组问题已经处理或失效。" } };
      }
      if (action === "input_local") {
        const completed = await this.completeUserInput(requestId, { kind: "local" });
        return {
          toast: {
            type: completed ? "success" : "warning",
            content: completed ? "已转回电脑端回答。" : "这组问题已经处理或失效。",
          },
        };
      }
      const questionId = actionValue?.questionId;
      const answer = actionValue?.answer;
      if (typeof questionId !== "string" || typeof answer !== "string") {
        return { toast: { type: "error", content: "答案参数不完整。" } };
      }
      const question = waiter.questions.find((item) => item.id === questionId);
      if (!question || !question.options.some((option) => option.label === answer)) {
        return { toast: { type: "error", content: "这个答案不属于当前问题。" } };
      }
      const completed = await this.completeUserInput(requestId, {
        kind: "answered",
        answers: { [questionId]: answer },
      });
      const runtime = runtimeDisplayName(this.store.getSession(waiter.sessionId)?.runtime);
      return {
        toast: {
          type: completed ? "success" : "warning",
          content: completed ? `已把答案交给 ${runtime}。` : "这组问题已经处理或失效。",
        },
      };
    }

    const resolution = actionToResolution(action);
    if (!resolution) {
      return { toast: { type: "warning", content: "无法识别这个操作。" } };
    }

    const approval = this.store.getApproval(requestId);
    if (
      !approval ||
      (typeof actionValue?.sessionId === "string" &&
        approval.sessionId !== actionValue.sessionId)
    ) {
      return { toast: { type: "error", content: "审批请求不存在或已失效。" } };
    }

    const completed = await this.completeApproval(requestId, resolution);
    return {
      toast: {
        type: completed ? "success" : "warning",
        content: completed
          ? approvalText(resolution, this.store.getSession(approval.sessionId))
          : "这条审批已经处理或失效。",
      },
    };
  }

  private isRuntimeAvailable(session: SessionRecord): boolean {
    if (session.status === "ended") {
      return false;
    }
    if (runtimeDefinition(session.runtime).transport === "http_event_stream") {
      return Boolean(this.opencode?.findInstanceBySession(session.sessionId));
    }
    return this.managedTerminals.isReady(session);
  }

  private isRuntimeStarting(session: SessionRecord): boolean {
    if (runtimeDefinition(session.runtime).transport === "http_event_stream") {
      return this.opencode?.hasPendingSession(session.sessionId) === true;
    }
    const normalizedCwd = normalizeRuntimeCwd(session.cwd);
    return this.managedTerminals.listOnline().some(
      (registration) =>
        normalizeRuntimeCwd(registration.cwd) === normalizedCwd &&
        registration.runtime === (session.runtime ?? "codex") &&
        (!registration.sessionId || registration.sessionId === session.sessionId),
    );
  }

  private async queueRuntimeLaunch(
    session: SessionRecord,
    item: PendingRuntimeLaunchPrompt,
  ): Promise<void> {
    const queue = this.pendingRuntimeLaunchPrompts.get(session.sessionId) ?? [];
    queue.push(item);
    this.pendingRuntimeLaunchPrompts.set(session.sessionId, queue);

    const existingRequestId = this.runtimeLaunchRequestIds.get(session.sessionId);
    const existingRequest = existingRequestId
      ? this.runtimeLaunchRequests.get(existingRequestId)
      : undefined;
    if (existingRequest) {
      await this.respond(
        item.sourceMessageId,
        item.chatId,
        `${runtimeDisplayName(session.runtime)} 会话正在自动恢复；这条消息会在窗口就绪后发送。`,
      );
      return;
    }

    const requestId = randomUUID();
    const timeoutMs = this.config.runtimeLaunchTimeoutMs ?? 2 * 60 * 1000;
    const timer = setTimeout(() => {
      const request = this.runtimeLaunchRequests.get(requestId);
      if (request) {
        void this.failRuntimeLaunch(
          request,
          "等待桌面助手自动打开窗口超时。请确认面板正在运行，然后在群里重试。",
        );
      }
    }, timeoutMs);
    timer.unref?.();
    const request: RuntimeLaunchRequest = {
      requestId,
      sessionId: session.sessionId,
      runtime: session.runtime ?? "codex",
      cwd: session.cwd,
      elevated: session.managedTerminalElevated === true,
      createdAt: new Date().toISOString(),
      status: this.isRuntimeStarting(session) ? "launched" : "pending",
      timer,
    };
    this.runtimeLaunchRequests.set(requestId, request);
    this.runtimeLaunchRequestIds.set(session.sessionId, requestId);
    await this.respond(
      item.sourceMessageId,
      item.chatId,
      request.status === "pending"
        ? `${runtimeDisplayName(session.runtime)} 窗口已关闭，正在请求电脑端自动恢复；这条消息会在窗口就绪后发送。`
        : `${runtimeDisplayName(session.runtime)} 窗口正在启动；这条消息会在窗口就绪后发送。`,
    );
  }

  private async failRuntimeLaunch(
    request: RuntimeLaunchRequest,
    detail: string,
  ): Promise<void> {
    clearTimeout(request.timer);
    this.runtimeLaunchRequests.delete(request.requestId);
    if (this.runtimeLaunchRequestIds.get(request.sessionId) === request.requestId) {
      this.runtimeLaunchRequestIds.delete(request.sessionId);
    }
    const queue = this.pendingRuntimeLaunchPrompts.get(request.sessionId) ?? [];
    this.pendingRuntimeLaunchPrompts.delete(request.sessionId);
    const session = this.store.getSession(request.sessionId);
    for (const item of queue) {
      await this.respond(
        item.sourceMessageId,
        item.chatId,
        notReceivedText(session ?? { runtime: request.runtime }, detail),
      );
    }
  }

  private async tryDrainRuntimeLaunch(sessionId: string): Promise<void> {
    const session = this.store.getSession(sessionId);
    if (!session || !this.isRuntimeAvailable(session)) {
      return;
    }
    const requestId = this.runtimeLaunchRequestIds.get(sessionId);
    const request = requestId ? this.runtimeLaunchRequests.get(requestId) : undefined;
    if (request) {
      clearTimeout(request.timer);
      this.runtimeLaunchRequests.delete(request.requestId);
      this.runtimeLaunchRequestIds.delete(sessionId);
    }
    const queue = this.pendingRuntimeLaunchPrompts.get(sessionId);
    if (!queue?.length) {
      this.pendingRuntimeLaunchPrompts.delete(sessionId);
      return;
    }
    this.pendingRuntimeLaunchPrompts.delete(sessionId);
    for (let index = 0; index < queue.length; index += 1) {
      const item = queue[index]!;
      const current = this.store.getSession(sessionId) ?? session;
      await this.resumeSession(
        current,
        item.prompt,
        item.sourceMessageId,
        item.chatId,
        item.queueRequested || index > 0,
        item.requestFileReturn,
      );
    }
  }

  private async resumeSession(
    session: SessionRecord,
    prompt: string,
    sourceMessageId: string,
    chatId: string,
    queueRequested = false,
    requestFileReturn = false,
  ): Promise<void> {
    if (this.store.hasPendingApprovalForSession(session.sessionId)) {
      await this.respond(
        sourceMessageId,
        chatId,
        codexNotReceived("请先处理待审批操作。"),
      );
      return;
    }
    if (this.hasPendingInputForSession(session.sessionId)) {
      await this.respond(
        sourceMessageId,
        chatId,
        codexNotReceived("请先回答待补充问题。"),
      );
      return;
    }
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
    const managedTerminal = this.managedTerminals.isManaged(session);
    if (managedTerminal && !this.managedTerminals.isReady(session)) {
      await this.respond(
        sourceMessageId,
        chatId,
        codexNotReceived("窗口尚未就绪。"),
      );
      return;
    }
    if (!managedTerminal &&
        (this.codex.isRunning(session.sessionId) ||
          this.remoteInputLocks.has(session.sessionId))) {
      const queue = this.runtimeQueues.get(session.sessionId) ?? [];
      queue.push({
        prompt,
        sourceMessageId,
        chatId,
        requestFileReturn,
      });
      this.runtimeQueues.set(session.sessionId, queue);
      await this.respond(
        sourceMessageId,
        chatId,
        receivedText(session),
      );
      return;
    }
    if (!managedTerminal) {
      this.remoteInputLocks.add(session.sessionId);
    }

    try {
      const runningSession = await this.store.upsertSession({
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
        const busy = this.remoteInputLocks.has(session.sessionId) ||
          session.status === "running";
        const submitMode = queueRequested && busy ? "queue" : "steer";
        if (submitMode === "queue") {
          this.managedQueueDepth.set(
            session.sessionId,
            (this.managedQueueDepth.get(session.sessionId) ?? 0) + 1,
          );
        }
        this.remoteInputLocks.add(session.sessionId);
        this.rememberRemotePrompt(session.sessionId, prompt);
        try {
          await this.managedTerminals.send(runningSession, prompt, submitMode);
        } catch (error) {
          this.forgetRemotePrompt(session.sessionId, prompt);
          if (submitMode === "queue") {
            this.decrementManagedQueueDepth(session.sessionId);
          }
          throw error;
        }
        if (requestFileReturn) {
          this.registerFileReturnRequest(
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
          await this.addRoute(ackId, session.sessionId, chatId, "resume_ack");
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
      this.remoteInputLocks.delete(session.sessionId);
      const message = error instanceof Error ? error.message : String(error);
      await this.store.upsertSession({
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

  private async startExternalPrompt(
    session: SessionRecord,
    item: QueuedRemotePrompt,
  ): Promise<void> {
    this.remoteInputLocks.add(session.sessionId);
    await this.codex.resume(session, item.prompt, async (result) => {
      const retrying = await this.handleCodexExit(session, result, item);
      if (!retrying) {
        await this.tryDrainExternalQueue(session.sessionId);
      }
    });
    if (item.requestFileReturn && item.retryAttempt === undefined) {
      this.registerFileReturnRequest(session.sessionId, item.chatId, 0);
    }
    if (item.retryAttempt === undefined) {
      const ackId = await this.respond(
        item.sourceMessageId,
        item.chatId,
        receivedText(session),
      );
      if (ackId) {
        await this.addRoute(ackId, session.sessionId, item.chatId, "resume_ack");
      }
    }
  }

  private async tryDrainExternalQueue(sessionId: string): Promise<void> {
    if (this.codex.isRunning(sessionId) || this.remoteInputLocks.has(sessionId)) {
      return;
    }
    const queue = this.runtimeQueues.get(sessionId);
    const item = queue?.shift();
    if (!item) {
      this.runtimeQueues.delete(sessionId);
      return;
    }
    if (queue?.length === 0) {
      this.runtimeQueues.delete(sessionId);
    }
    const session = this.store.getSession(sessionId);
    if (!session || session.status === "ended") {
      await this.respond(
        item.sourceMessageId,
        item.chatId,
        "排队消息未执行：对应的外部 Codex 会话已经结束。",
      );
      return;
    }
    try {
      const runningSession = await this.store.upsertSession({
        sessionId: session.sessionId,
        alias: session.alias,
        cwd: session.cwd,
        model: session.model,
        status: "running",
        source: session.source,
      });
      await this.startExternalPrompt(runningSession, item);
    } catch (error) {
      this.remoteInputLocks.delete(sessionId);
      const message = error instanceof Error ? error.message : String(error);
      await this.respond(
        item.sourceMessageId,
        item.chatId,
        `排队消息启动失败：${truncate(message, 500)}`,
      );
      void this.tryDrainExternalQueue(sessionId);
    }
  }

  private async resumeOpenCodeSession(
    session: SessionRecord,
    prompt: string,
    sourceMessageId: string,
    chatId: string,
    requestFileReturn: boolean,
  ): Promise<void> {
    if (!this.opencode?.findInstanceBySession(session.sessionId)) {
      await this.respond(
        sourceMessageId,
        chatId,
        notReceivedText(session, "opencode 窗口未连接。"),
      );
      return;
    }
    if (
      this.remoteInputLocks.has(session.sessionId) ||
      session.status === "running"
    ) {
      const queue = this.runtimeQueues.get(session.sessionId) ?? [];
      queue.push({ prompt, sourceMessageId, chatId, requestFileReturn });
      this.runtimeQueues.set(session.sessionId, queue);
      await this.respond(sourceMessageId, chatId, receivedText(session));
      return;
    }
    this.remoteInputLocks.add(session.sessionId);
    try {
      const runningSession = await this.store.upsertSession({
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
      await this.opencode.sendPrompt(session.sessionId, prompt);
      if (requestFileReturn) {
        this.registerFileReturnRequest(session.sessionId, chatId, 0);
      }
      const ackId = await this.respond(
        sourceMessageId,
        chatId,
        receivedText(runningSession),
      );
      if (ackId) {
        await this.addRoute(ackId, session.sessionId, chatId, "resume_ack");
      }
    } catch (error) {
      this.remoteInputLocks.delete(session.sessionId);
      this.forgetRemotePrompt(session.sessionId, prompt);
      const message = error instanceof Error ? error.message : String(error);
      await this.store.upsertSession({
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

  private async tryDrainOpenCodeQueue(sessionId: string): Promise<void> {
    if (this.remoteInputLocks.has(sessionId)) {
      return;
    }
    const queue = this.runtimeQueues.get(sessionId);
    const item = queue?.shift();
    if (!item) {
      this.runtimeQueues.delete(sessionId);
      return;
    }
    if (queue?.length === 0) {
      this.runtimeQueues.delete(sessionId);
    }
    const session = this.store.getSession(sessionId);
    if (
      !session ||
      runtimeDefinition(session.runtime).transport !== "http_event_stream" ||
      session.status === "ended"
    ) {
      await this.respond(
        item.sourceMessageId,
        item.chatId,
        notReceivedText({ runtime: "opencode" }, "对应的 opencode 窗口已经关闭。"),
      );
      return;
    }
    if (!this.opencode?.findInstanceBySession(sessionId)) {
      this.remoteInputLocks.add(sessionId);
      await this.respond(
        item.sourceMessageId,
        item.chatId,
        notReceivedText(session, "opencode 窗口未连接。"),
      );
      this.remoteInputLocks.delete(sessionId);
      void this.tryDrainOpenCodeQueue(sessionId);
      return;
    }
    this.remoteInputLocks.add(sessionId);
    try {
      const runningSession = await this.store.upsertSession({
        sessionId: session.sessionId,
        alias: session.alias,
        cwd: session.cwd,
        model: session.model,
        status: "running",
        runtime: "opencode",
        managedByAssistant: true,
      });
      this.rememberRemotePrompt(session.sessionId, item.prompt);
      await this.opencode.sendPrompt(session.sessionId, item.prompt);
      if (item.requestFileReturn) {
        this.registerFileReturnRequest(session.sessionId, item.chatId, 0);
      }
      const ackId = await this.respond(
        item.sourceMessageId,
        item.chatId,
        receivedText(runningSession),
      );
      if (ackId) {
        await this.addRoute(ackId, session.sessionId, item.chatId, "resume_ack");
      }
    } catch (error) {
      this.remoteInputLocks.delete(sessionId);
      this.forgetRemotePrompt(sessionId, item.prompt);
      const message = error instanceof Error ? error.message : String(error);
      await this.respond(
        item.sourceMessageId,
        item.chatId,
        notReceivedText(session, truncate(message, 500)),
      );
      void this.tryDrainOpenCodeQueue(sessionId);
    }
  }

  private async forgetOpenCodeSession(
    sessionId: string,
    reason: string,
  ): Promise<void> {
    const session = this.store.getSession(sessionId);
    if (session && session.status !== "ended") {
      await this.store.upsertSession({
        sessionId,
        cwd: session.cwd,
        model: session.model,
        status: "ended",
        runtime: "opencode",
        managedByAssistant: true,
      });
    }
    this.remoteInputLocks.delete(sessionId);
    const queued = this.runtimeQueues.get(sessionId);
    this.runtimeQueues.delete(sessionId);
    this.pendingRemotePrompts.delete(sessionId);
    this.fileReturnRequests.delete(sessionId);
    this.managedRetryCounts.delete(sessionId);
    this.opencodeToolParts.delete(sessionId);
    if (session) {
      void this.finishActivity(sessionId, reason).catch((error) => {
        console.warn("[opencode] Could not finalize activity:", error);
      });
    }
    this.opencode?.forgetSession(sessionId);
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

  private async recordOpenCodeActivity(
    sessionId: string,
    input: {
      hook_event_name: "PreToolUse" | "PostToolUse" | "PreCompact";
      cwd: string;
      turnId?: string;
      tool_name?: string;
      tool_preview?: string;
      tool_response_preview?: string;
    },
  ): Promise<void> {
    const payload: ActivityHookPayload = {
      hook_event_name: input.hook_event_name,
      session_id: sessionId,
      turn_id: input.turnId,
      cwd: input.cwd,
      model: undefined,
      prompt: undefined,
      tool_name: input.tool_name,
      tool_preview: input.tool_preview,
      tool_response_preview: input.tool_response_preview,
    };
    await this.recordActivity(payload);
  }

  private async handleCodexExit(
    session: SessionRecord,
    result: CodexExitResult,
    item: QueuedRemotePrompt,
  ): Promise<boolean> {
    this.remoteInputLocks.delete(session.sessionId);
    if (result.code === 0) {
      return false;
    }
    const reason =
      result.stderr ||
      (result.signal
        ? `Codex 进程被信号 ${result.signal} 终止。`
        : `Codex 进程退出，代码 ${String(result.code)}。`);
    const failedSession = await this.store.upsertSession({
      sessionId: session.sessionId,
      cwd: session.cwd,
      model: session.model,
      status: "error",
      error: reason,
    });
    const attempt = item.retryAttempt ?? 0;
    const retrySettings = this.store.getSettings();
    const retryDelay = retryDelayMs(retrySettings, this.config.retryBaseDelayMs);
    const retrying =
      retrySettings.autoRetryErrors &&
      isRetryableCodexError(reason) &&
      attempt < retrySettings.retryMaxAttempts;
    const detail = retrying
      ? `${reason}\n\n助手将在 ${Math.ceil(retryDelay / 1_000)} 秒后自动重试（第 ${attempt + 1}/${retrySettings.retryMaxAttempts} 次）。`
      : reason;
    for (const recipient of await this.notificationRecipients(failedSession)) {
      try {
        for (const card of buildErrorCards(failedSession, detail)) {
          const messageId = await this.feishu.sendCard(recipient.chatId, card);
          await this.addRoute(messageId, session.sessionId, recipient.chatId, "error");
        }
      } catch (error) {
        console.error("[resume] Failed to send an error notification:", error);
      }
    }
    if (retrying) {
      this.remoteInputLocks.add(session.sessionId);
      setTimeout(() => {
        const current = this.store.getSession(session.sessionId);
        const currentSettings = this.store.getSettings();
        if (
          !currentSettings.autoRetryErrors ||
          attempt >= currentSettings.retryMaxAttempts ||
          !current ||
          current.status === "ended"
        ) {
          this.remoteInputLocks.delete(session.sessionId);
          void this.tryDrainExternalQueue(session.sessionId);
          return;
        }
        void this.startExternalPrompt(current, {
          ...item,
          retryAttempt: attempt + 1,
        }).catch((error) => {
          this.remoteInputLocks.delete(session.sessionId);
          console.error("[retry] External retry failed:", error);
          void this.tryDrainExternalQueue(session.sessionId);
        });
      }, retryDelay);
    }
    return retrying;
  }

  private hasPendingInputForSession(sessionId: string): boolean {
    return [...this.inputWaiters.values()].some(
      (waiter) => waiter.sessionId === sessionId,
    );
  }

  private async completeUserInput(
    requestId: string,
    resolution: UserInputResolution,
  ): Promise<boolean> {
    const waiter = this.inputWaiters.get(requestId);
    if (!waiter) {
      return false;
    }
    clearTimeout(waiter.timer);
    this.inputWaiters.delete(requestId);
    const session = await this.store.upsertSession({
      sessionId: waiter.sessionId,
      cwd: waiter.cwd,
      turnId: waiter.turnId,
      status: resolution.kind === "answered" ? "running" : "waiting",
    });
    waiter.resolve(resolution);
    const card = buildResolvedUserInputCard(
      session,
      waiter.questions,
      resolution.kind === "answered" ? resolution.answers : undefined,
      resolution.kind,
    );
    void Promise.allSettled(
      waiter.messageIds.map((messageId) => this.feishu.patchCard(messageId, card)),
    );
    console.log(`[input] ${resolution.kind} for session #${session.shortId}.`);
    return true;
  }

  private resolveInputsForSession(
    sessionId: string,
    resolution: "local" | "timeout",
  ): void {
    for (const [requestId, waiter] of this.inputWaiters) {
      if (waiter.sessionId === sessionId) {
        void this.completeUserInput(requestId, { kind: resolution });
      }
    }
  }

  private async recordActivity(payload: ActivityHookPayload): Promise<void> {
    const current = this.store.getSession(payload.session_id);
    const isRemoteQuestion = payload.hook_event_name === "PreToolUse" &&
      payload.tool_name === "request_user_input";
    const session = !current ||
        (!isRemoteQuestion &&
          (current.status !== "running" ||
            (payload.turn_id && current.lastTurnId !== payload.turn_id)))
      ? await this.store.upsertSession({
          sessionId: payload.session_id,
          cwd: payload.cwd,
          model: payload.model,
          turnId: payload.turn_id,
          status: "running",
          runtime: payload.runtime,
          ...(payload.managed_terminal_id !== undefined
            ? { managedTerminalId: payload.managed_terminal_id }
            : {}),
          ...(payload.managed_terminal_elevated !== undefined
            ? { managedTerminalElevated: payload.managed_terminal_elevated }
            : {}),
        })
      : current;
    let state = this.activityStates.get(payload.session_id);
    if (state && payload.turn_id && state.turnId && state.turnId !== payload.turn_id) {
      await this.finishActivity(payload.session_id, "上一轮已结束");
      state = undefined;
    }
    if (!state) {
      state = {
        sessionId: payload.session_id,
        turnId: payload.turn_id,
        startedAt: new Date().toISOString(),
        events: [],
        messageIds: new Map(),
        lastSentAt: 0,
        revision: 0,
        sentRevision: -1,
        completed: false,
      };
      this.activityStates.set(payload.session_id, state);
    }
    state.turnId ??= payload.turn_id;
    state.events.push(activityEventFromPayload(payload));
    state.events = state.events.slice(-6);
    state.revision += 1;
    this.scheduleActivityFlush(session.sessionId);
  }

  private async handleUserPromptSubmit(
    payload: ActivityHookPayload,
    settings: BridgeSettings,
  ): Promise<void> {
    const prompt = payload.prompt;
    if (!prompt?.trim()) {
      return;
    }
    if (this.consumeRemotePrompt(payload.session_id, prompt)) {
      return;
    }
    if (!settings.notifyUserPrompts) {
      return;
    }
    const session = this.store.getSession(payload.session_id);
    if (session?.managedByAssistant !== true) {
      return;
    }
    for (const recipient of await this.notificationRecipients(session)) {
      try {
        for (const card of buildUserPromptCards(session, prompt)) {
          const messageId = await this.feishu.sendCard(recipient.chatId, card);
          await this.addRoute(messageId, session.sessionId, recipient.chatId, "user_prompt");
        }
      } catch (error) {
        console.error("[prompt] Failed to send a PC prompt card:", error);
      }
    }
  }

  private rememberRemotePrompt(sessionId: string, prompt: string): void {
    const now = Date.now();
    const queue = (this.pendingRemotePrompts.get(sessionId) ?? [])
      .filter((item) => now - item.createdAt <= 60_000);
    queue.push({ prompt: normalizePromptForMatch(prompt), createdAt: now });
    this.pendingRemotePrompts.set(sessionId, queue.slice(-12));
  }

  private forgetRemotePrompt(sessionId: string, prompt: string): void {
    const queue = this.pendingRemotePrompts.get(sessionId);
    if (!queue) return;
    const normalized = normalizePromptForMatch(prompt);
    const index = queue.findIndex((item) => item.prompt === normalized);
    if (index >= 0) queue.splice(index, 1);
    if (queue.length === 0) this.pendingRemotePrompts.delete(sessionId);
  }

  private consumeRemotePrompt(sessionId: string, prompt: string): boolean {
    const queue = this.pendingRemotePrompts.get(sessionId);
    if (!queue) return false;
    const now = Date.now();
    const normalized = normalizePromptForMatch(prompt);
    const fresh = queue.filter((item) => now - item.createdAt <= 60_000);
    const index = fresh.findIndex((item) => item.prompt === normalized);
    if (index < 0) {
      if (fresh.length === 0) this.pendingRemotePrompts.delete(sessionId);
      else this.pendingRemotePrompts.set(sessionId, fresh);
      return false;
    }
    fresh.splice(index, 1);
    if (fresh.length === 0) this.pendingRemotePrompts.delete(sessionId);
    else this.pendingRemotePrompts.set(sessionId, fresh);
    return true;
  }

  private scheduleActivityFlush(sessionId: string): void {
    const state = this.activityStates.get(sessionId);
    if (!state || state.timer || state.completed) return;
    const delay = Math.max(0, 2_000 - (Date.now() - state.lastSentAt));
    state.timer = setTimeout(() => {
      state.timer = undefined;
      void this.flushActivity(sessionId).catch((error) => {
        console.error("[activity] Could not update Feishu progress card:", error);
      });
    }, delay);
  }

  private async flushActivity(sessionId: string, force = false): Promise<void> {
    const state = this.activityStates.get(sessionId);
    if (!state) return;
    if (state.flushing) {
      await state.flushing;
      if (force && state.sentRevision < state.revision) {
        await this.flushActivity(sessionId, true);
      }
      return;
    }
    if (!force && state.sentRevision >= state.revision) return;
    const capturedRevision = state.revision;
    const operation = (async () => {
      const session = this.store.getSession(sessionId);
      if (!session) return;
      const card = buildActivityCard(
        session,
        state.events,
        state.startedAt,
        state.completed,
      );
      for (const recipient of await this.notificationRecipients(session)) {
        try {
          const existingMessageId = state.messageIds.get(recipient.chatId);
          if (existingMessageId) {
            await this.feishu.patchCard(existingMessageId, card);
          } else {
            const messageId = await this.feishu.sendCard(recipient.chatId, card);
            state.messageIds.set(recipient.chatId, messageId);
            await this.addRoute(messageId, sessionId, recipient.chatId, "activity");
          }
        } catch (error) {
          console.error("[activity] Failed to send or patch a progress card:", error);
        }
      }
      state.lastSentAt = Date.now();
      state.sentRevision = capturedRevision;
    })();
    state.flushing = operation;
    try {
      await operation;
    } finally {
      state.flushing = undefined;
    }
    if (!state.completed && state.sentRevision < state.revision) {
      this.scheduleActivityFlush(sessionId);
    }
  }

  private async finishActivity(sessionId: string, label: string): Promise<void> {
    const state = this.activityStates.get(sessionId);
    if (!state) return;
    if (state.timer) {
      clearTimeout(state.timer);
      state.timer = undefined;
    }
    if (!state.completed) {
      state.completed = true;
      state.events.push({ at: new Date().toISOString(), label });
      state.events = state.events.slice(-6);
      state.revision += 1;
    }
    await this.flushActivity(sessionId, true);
    this.activityStates.delete(sessionId);
  }

  private attachmentKey(openId: string, chatId: string): string {
    return `${openId}\u0000${chatId}`;
  }

  private stageAttachments(key: string, files: SavedAttachment[]): void {
    this.pruneStagedAttachments();
    const current = this.pendingAttachments.get(key)?.files ?? [];
    const limit = Math.max(1, this.config.inboundAttachmentMaxCount * 2);
    this.pendingAttachments.set(key, {
      createdAt: Date.now(),
      files: [...current, ...files].slice(-limit),
    });
  }

  private peekAttachments(key: string): SavedAttachment[] {
    this.pruneStagedAttachments();
    return this.pendingAttachments.get(key)?.files ?? [];
  }

  private takeAttachments(key: string): SavedAttachment[] {
    this.pruneStagedAttachments();
    const staged = this.pendingAttachments.get(key);
    this.pendingAttachments.delete(key);
    return staged?.files ?? [];
  }

  private pruneStagedAttachments(): void {
    const cutoff = Date.now() - this.config.uploadTtlMs;
    for (const [key, staged] of this.pendingAttachments) {
      if (staged.createdAt < cutoff) {
        this.pendingAttachments.delete(key);
      }
    }
  }

  private registerFileReturnRequest(
    sessionId: string,
    chatId: string,
    remainingStops: number,
  ): void {
    const now = Date.now();
    const requests = (this.fileReturnRequests.get(sessionId) ?? []).filter(
      (request) => request.expiresAt > now,
    );
    requests.push({
      chatId,
      remainingStops: Math.max(0, remainingStops),
      expiresAt: now + 2 * 60 * 60 * 1000,
    });
    this.fileReturnRequests.set(sessionId, requests);
  }

  private advanceFileReturnRequests(sessionId: string): FileReturnRequest | undefined {
    const now = Date.now();
    const requests = (this.fileReturnRequests.get(sessionId) ?? []).filter(
      (request) => request.expiresAt > now,
    );
    const eligibleIndex = requests.findIndex((request) => request.remainingStops === 0);
    const eligible = eligibleIndex >= 0 ? requests.splice(eligibleIndex, 1)[0] : undefined;
    for (const request of requests) {
      if (request.remainingStops > 0) {
        request.remainingStops -= 1;
      }
    }
    if (requests.length > 0) {
      this.fileReturnRequests.set(sessionId, requests);
    } else {
      this.fileReturnRequests.delete(sessionId);
    }
    return eligible;
  }

  private decrementManagedQueueDepth(sessionId: string): void {
    const current = this.managedQueueDepth.get(sessionId) ?? 0;
    if (current <= 1) {
      this.managedQueueDepth.delete(sessionId);
    } else {
      this.managedQueueDepth.set(sessionId, current - 1);
    }
  }

  private async sendRequestedFiles(
    session: SessionRecord,
    chatId: string,
    candidates: string[],
  ): Promise<void> {
    const errors: string[] = [];
    let sentCount = 0;
    for (const candidate of candidates.slice(0, 3)) {
      try {
        const file = await validateBridgeFile(
          candidate,
          session.cwd,
          this.config.outboundFileMaxBytes,
        );
        const messageId = await this.feishu.sendLocalFile(chatId, file.path);
        await this.addRoute(messageId, session.sessionId, chatId, "stop");
        sentCount += 1;
      } catch (error) {
        const detail = error instanceof Error ? error.message : String(error);
        errors.push(`${candidate}：${detail}`);
      }
    }
    if (errors.length > 0) {
      await this.feishu.sendText(
        chatId,
        `文件回传结果：成功 ${sentCount} 个，失败 ${errors.length} 个。\n${errors
          .map((error) => `- ${truncate(error, 400)}`)
          .join("\n")}`,
      );
    }
  }

  private async completeApproval(
    requestId: string,
    resolution: ApprovalResolution,
  ): Promise<boolean> {
    const approval = await this.store.resolveApproval(requestId, resolution);
    if (!approval) {
      return false;
    }

    const waiter = this.approvalWaiters.get(requestId);
    if (waiter) {
      clearTimeout(waiter.timer);
      this.approvalWaiters.delete(requestId);
    }

    const session = await this.store.upsertSession({
      sessionId: approval.sessionId,
      cwd: approval.cwd,
      turnId: approval.turnId,
      status:
        resolution === "allow"
          ? "running"
          : resolution === "deny"
            ? "waiting"
            : "local_approval",
    });

    const card = buildResolvedApprovalCard(session, approval, resolution);
    waiter?.resolve(resolution);
    void Promise.allSettled(
      approval.messageIds.map((messageId) => this.feishu.patchCard(messageId, card)),
    );
    if (approval.opencodePermissionId && (resolution === "allow" || resolution === "deny")) {
      try {
        await this.opencode?.replyPermission(
          approval.sessionId,
          approval.opencodePermissionId,
          resolution === "allow" ? "once" : "reject",
        );
      } catch (error) {
        console.error("[approval] Failed to forward decision to opencode:", error);
      }
    }
    console.log(
      `[approval] ${resolution} for session #${session.shortId}.`,
    );
    return true;
  }

  private listApprovalViews(): Array<Record<string, unknown>> {
    const now = Date.now();
    const recentCutoff = now - 10 * 60 * 1000;
    return this.store
      .listApprovals()
      .filter(
        (approval) =>
          approval.status === "pending" ||
          (approval.resolvedAt !== undefined &&
            Date.parse(approval.resolvedAt) >= recentCutoff),
      )
      .map((approval) => {
        const session = this.store.getSession(approval.sessionId);
        return {
          requestId: approval.requestId,
          sessionId: approval.sessionId,
          sessionLabel: session
            ? sessionLabel(session)
            : "#" + shortSessionId(approval.sessionId),
          projectName: session?.projectName ?? projectNameFromCwd(approval.cwd),
          cwd: approval.cwd,
          toolName: approval.toolName,
          toolPreview: approval.toolPreview,
          createdAt: approval.createdAt,
          expiresAt: approval.expiresAt,
          status: approval.status,
          resolution: approval.resolution ?? "",
          resolvedAt: approval.resolvedAt ?? "",
        };
      });
  }

  private listActiveSessions(): SessionRecord[] {
    const now = Date.now();
    const registrations = this.managedTerminals.listOnline(now);
    const registrationById = new Map(
      registrations.map((registration) => [registration.terminalId, registration]),
    );
    const openSessions = this.store.listOpenSessions();
    const trackedClients = openSessions.flatMap((session): ClientProcessMetadata[] =>
      session.clientProcessId
        ? [{
            processId: session.clientProcessId,
            startedAt: session.clientProcessStartedAt,
          }]
        : []
    );
    const liveClientProcessIds = (
      this.config.liveClientProcessIds ?? captureLiveTrackedCodexProcessIds
    )(trackedClients);
    const sessions = openSessions
      .flatMap((session): SessionRecord[] => {
        if (runtimeDefinition(session.runtime).transport === "http_event_stream") {
          const instance = this.opencode?.findInstanceBySession(session.sessionId);
          return instance ? [session] : [];
        }
        if (!this.managedTerminals.isManaged(session)) {
          if (session.clientProcessId) {
            return liveClientProcessIds.has(session.clientProcessId) ? [session] : [];
          }
          const fallbackMs = Math.min(
            this.config.sessionActiveMs,
            5 * 60 * 1000,
          );
          return now - Date.parse(session.lastSeenAt) <= fallbackMs ? [session] : [];
        }
        const terminalId = session.managedTerminalId;
        const registration = terminalId
          ? registrationById.get(terminalId)
          : undefined;
        return registration
          ? [{ ...session, lastSeenAt: new Date(registration.lastSeenAt).toISOString() }]
          : [];
      });

    const representedTerminals = new Set(
      sessions
        .map((session) => session.managedTerminalId)
        .filter((terminalId): terminalId is string => Boolean(terminalId)),
    );
    for (const registration of registrations) {
      if (representedTerminals.has(registration.terminalId)) {
        continue;
      }
      sessions.push({
        sessionId: managedTerminalSessionId(registration.terminalId),
        shortId: shortSessionId(registration.terminalId),
        cwd: registration.cwd,
        projectName: projectNameFromCwd(registration.cwd),
        status: registration.ready ? "ready" : "starting",
        openedAt: new Date(registration.createdAt).toISOString(),
        lastSeenAt: new Date(registration.lastSeenAt).toISOString(),
        source: "managed_window",
        runtime: registration.runtime,
        managedTerminalId: registration.terminalId,
        managedTerminalElevated: registration.elevated,
      });
    }
    return sessions.sort(
      (left, right) => Date.parse(right.lastSeenAt) - Date.parse(left.lastSeenAt),
    );
  }

  private findActiveSessionsByShortToken(token: string): SessionRecord[] {
    const normalized = token.replace(/[^a-zA-Z0-9]/g, "").toLowerCase();
    if (normalized.length < 4) {
      return [];
    }
    return this.listActiveSessions().filter((session) =>
      session.sessionId
        .replace(/[^a-zA-Z0-9]/g, "")
        .toLowerCase()
        .endsWith(normalized),
    );
  }

  private findActiveSessionsByAlias(alias: string): SessionRecord[] {
    const key = sessionAliasKey(alias);
    if (!key) {
      return [];
    }
    return this.listActiveSessions().filter(
      (session) => session.alias && sessionAliasKey(session.alias) === key,
    );
  }

  private async updateSessionAlias(
    session: SessionRecord,
    rawAlias: string | undefined,
  ): Promise<SessionAliasResult> {
    let persistentSession = this.store.getSession(session.sessionId);
    if (!persistentSession && session.source === "managed_window") {
      persistentSession = await this.store.upsertSession({
        sessionId: session.sessionId,
        cwd: session.cwd,
        status: "ready",
        source: session.source,
        managedTerminalId: session.managedTerminalId,
        managedTerminalElevated: session.managedTerminalElevated,
      });
    }
    if (!persistentSession) {
      return { ok: false, error: "会话不存在或已经失效。" };
    }

    if (rawAlias === undefined || !rawAlias.trim()) {
      const updated = await this.store.setSessionAlias(
        persistentSession.sessionId,
        undefined,
      );
      if (updated?.feishuChatId) {
        await this.renameSessionGroup(updated).catch((error) => {
          console.warn("[feishu] Could not rename session group:", error);
        });
      }
      return updated
        ? { ok: true, session: updated }
        : { ok: false, error: "会话不存在或已经失效。" };
    }

    const validationError = sessionAliasValidationError(rawAlias);
    if (validationError) {
      return { ok: false, error: validationError };
    }
    const alias = normalizeSessionAlias(rawAlias);
    const key = sessionAliasKey(alias);
    const conflict = this.listActiveSessions().find(
      (item) =>
        item.sessionId !== session.sessionId &&
        item.alias &&
        sessionAliasKey(item.alias) === key,
    );
    if (conflict) {
      return {
        ok: false,
        error: `别名 @${alias} 已被会话 ${conflict.projectName} #${conflict.shortId} 使用。`,
      };
    }

    const updated = await this.store.setSessionAlias(persistentSession.sessionId, alias);
    if (updated?.feishuChatId) {
      await this.renameSessionGroup(updated).catch((error) => {
        console.warn("[feishu] Could not rename session group:", error);
      });
    }
    return updated
      ? { ok: true, session: updated }
      : { ok: false, error: "会话不存在或已经失效。" };
  }

  private async handleFeishuAliasCommand(
    command: AliasCommand,
    messageId: string,
    chatId: string,
  ): Promise<void> {
    if (!command.targetKind || !command.target) {
      await this.respond(messageId, chatId, this.formatAliasList());
      return;
    }

    const matches = command.targetKind === "short"
      ? this.findActiveSessionsByShortToken(command.target)
      : this.findActiveSessionsByAlias(command.target);
    const address = command.targetKind === "short"
      ? `#${command.target}`
      : `@${command.target}`;
    if (matches.length !== 1) {
      await this.respond(
        messageId,
        chatId,
        matches.length === 0
          ? `没有找到 ${address} 对应的活跃会话。发送“会话”查看列表。`
          : `${address} 匹配到多个会话，请换用完整短 ID。`,
      );
      return;
    }

    const session = matches[0];
    if (command.alias === undefined) {
      await this.respond(
        messageId,
        chatId,
        session.alias
          ? `会话 ${session.projectName} #${session.shortId} 的别名是 @${session.alias}。`
          : `会话 ${session.projectName} #${session.shortId} 尚未设置别名。`,
      );
      return;
    }

    const clear = ["清除", "删除", "clear", "none"].includes(
      command.alias.trim().toLowerCase(),
    );
    const result = await this.updateSessionAlias(
      session,
      clear ? undefined : command.alias,
    );
    if (!result.ok || !result.session) {
      await this.respond(messageId, chatId, result.error ?? "设置别名失败。");
      return;
    }

    await this.respond(
      messageId,
      chatId,
      result.session.alias
        ? `已将 ${result.session.projectName} #${result.session.shortId} 的别名设为 @${result.session.alias}。以后可发送“@${result.session.alias} 回复内容”。`
        : `已清除 ${result.session.projectName} #${result.session.shortId} 的别名。`,
    );
  }

  private activeSessionDefinition(): string {
    const fallbackMs = Math.min(this.config.sessionActiveMs, 5 * 60 * 1000);
    return `活跃定义：助手打开的 Codex / Claude Code 窗口从打开到关闭始终算活跃；opencode 窗口从连接到关闭始终算活跃；外部会话会跟踪真实 CLI 进程，进程关闭后自动移除。无法取得进程信息时仅临时保留 ${formatDuration(fallbackMs)}。`;
  }

  private formatSessionList(): string {
    const sessions = this.listActiveSessions();
    if (sessions.length === 0) {
      return `当前没有活跃助手会话。\n${this.activeSessionDefinition()}`;
    }
    const lines = sessions.slice(0, 20).map(
      (session, index) => {
        const kind = runtimeDisplayName(session.runtime);
        const runtime = runtimeDefinition(session.runtime);
        const mode = runtime.transport === "http_event_stream"
          ? ` · ${kind} 窗口`
          : session.managedTerminalId
            ? session.managedTerminalElevated
              ? ` · ${kind} 管理员同步`
              : ` · ${kind} 窗口同步`
            : ` · ${kind} 外部会话（仅通知）`;
        const address = session.alias
          ? `@${session.alias}  (#${session.shortId})`
          : sessionAddress(session);
        const queued = (this.runtimeQueues.get(session.sessionId)?.length ?? 0) +
          (this.managedQueueDepth.get(session.sessionId) ?? 0);
        return `${index + 1}. ${address}  ${session.projectName}  · ${statusLabel(session.status)}${mode}${queued > 0 ? ` · 排队 ${queued}` : ""}`;
      },
    );
    return `当前活跃会话：\n${lines.join("\n")}\n\n回复：@别名 内容；排队：排队 @别名 内容；文件回传：发文件 @别名 要求\n${this.activeSessionDefinition()}`;
  }

  private formatAliasList(): string {
    const sessions = this.listActiveSessions();
    if (sessions.length === 0) {
      return `当前没有可设置别名的活跃会话。\n\n${aliasCommandUsage()}`;
    }
    const lines = sessions.slice(0, 20).map(
      (session, index) =>
        `${index + 1}. ${session.alias ? `@${session.alias}` : "（未设置）"} · #${session.shortId} · ${session.projectName}`,
    );
    return `当前会话别名：\n${lines.join("\n")}\n\n${aliasCommandUsage()}`;
  }

  private uniqueChatBindings(): Binding[] {
    const byChat = new Map<string, Binding>();
    for (const binding of this.store.listBindings()) {
      byChat.set(binding.chatId, binding);
    }
    return [...byChat.values()];
  }

  private async notificationRecipients(
    session: SessionRecord,
  ): Promise<Array<{ chatId: string; binding?: Binding }>> {
    if (session.managedByAssistant === true) {
      const ensured = await this.ensureSessionGroup(session.sessionId);
      if (ensured?.feishuChatId) {
        return [{ chatId: ensured.feishuChatId }];
      }
    }
    return this.uniqueChatBindings().map((binding) => ({
      chatId: binding.chatId,
      binding,
    }));
  }

  private async ensureSessionGroup(
    sessionId: string,
    forceRetry = false,
  ): Promise<SessionRecord | undefined> {
    const session = this.store.getSession(sessionId);
    if (!session || session.managedByAssistant !== true) {
      return session;
    }
    if (session.feishuChatId) {
      return session;
    }
    // Persisted failures are retried only from the desktop action. This keeps
    // ordinary Codex notifications from repeatedly calling the create-chat
    // API while permissions are still missing.
    if (session.feishuChatError && !forceRetry) {
      return session;
    }
    const ownerOpenId = this.store.getOwnerOpenId();
    if (!ownerOpenId) {
      return session;
    }
    const pending = this.sessionGroupCreates.get(sessionId);
    if (pending) {
      return await pending;
    }
    const operation = this.createSessionGroup(session, ownerOpenId);
    this.sessionGroupCreates.set(sessionId, operation);
    try {
      return await operation;
    } finally {
      if (this.sessionGroupCreates.get(sessionId) === operation) {
        this.sessionGroupCreates.delete(sessionId);
      }
    }
  }

  private async createSessionGroup(
    session: SessionRecord,
    ownerOpenId: string,
  ): Promise<SessionRecord | undefined> {
    const name = this.sessionGroupName(session);
    const kind = runtimeDisplayName(session.runtime);
    try {
      const group = await this.feishu.createSessionGroup(
        ownerOpenId,
        name,
        `${kind} 会话 ${session.shortId} · ${session.cwd}`,
      );
      const updated = await this.store.setSessionFeishuChat(session.sessionId, {
        chatId: group.chatId,
        chatName: group.name,
      });
      if (updated) {
        try {
          await this.feishu.sendText(
            group.chatId,
            `已连接到 ${sessionLabel(updated)}。以后这个群里的消息都会发送到对应 ${kind} 窗口。`,
          );
        } catch (error) {
          console.warn("[feishu] Session group created, but welcome message failed:", error);
        }
      }
      console.log(`[feishu] Created session group ${group.chatId} for #${session.shortId}.`);
      return updated ?? session;
    } catch (error) {
      const detail = truncate(error instanceof Error ? error.message : String(error), 500);
      await this.store.setSessionFeishuChatError(session.sessionId, detail);
      console.warn(`[feishu] Could not create session group for #${session.shortId}: ${detail}`);
      return this.store.getSession(session.sessionId) ?? session;
    }
  }

  private async renameSessionGroup(session: SessionRecord): Promise<void> {
    if (!session.feishuChatId) {
      return;
    }
    const name = this.sessionGroupName(session);
    await this.feishu.updateSessionGroupName(session.feishuChatId, name);
    await this.store.setSessionFeishuChat(session.sessionId, {
      chatId: session.feishuChatId,
      chatName: name,
      createdAt: session.feishuChatCreatedAt,
    });
  }

  private sessionGroupName(session: SessionRecord): string {
    const prefix = runtimeGroupPrefix(session.runtime);
    return `${prefix}${session.alias || session.projectName || session.shortId}`.slice(0, 60);
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

function parseBindCommand(
  text: string,
  command: string,
): { matched: boolean; code?: string } {
  if (text === command) {
    return { matched: true };
  }
  const prefix = `${command} `;
  if (!text.startsWith(prefix)) {
    return { matched: false };
  }
  const code = text.slice(prefix.length).trim();
  return { matched: true, code: code || undefined };
}

function parsePromptDirectives(text: string): {
  prompt: string;
  queue: boolean;
  fileReturn: boolean;
} {
  let prompt = text.trim();
  let queue = false;
  let fileReturn = false;
  for (let index = 0; index < 3; index += 1) {
    const queueMatch = prompt.match(/^(?:排队|\/queue|queue)\s+([\s\S]+)$/iu);
    if (queueMatch?.[1]) {
      queue = true;
      prompt = queueMatch[1].trim();
      continue;
    }
    const fileMatch = prompt.match(/^(?:发文件|\/sendfile|sendfile)\s+([\s\S]+)$/iu);
    if (fileMatch?.[1]) {
      fileReturn = true;
      prompt = fileMatch[1].trim();
      continue;
    }
    break;
  }
  return { prompt, queue, fileReturn };
}

function parseUserInputAnswers(
  text: string,
  questions: UserInputQuestion[],
): Record<string, string> | undefined {
  const parts = questions.length === 1
    ? [text.trim()]
    : text.split(/[；;\n]+/).map((part) => part.trim()).filter(Boolean);
  if (parts.length !== questions.length) {
    return undefined;
  }
  const answers: Record<string, string> = {};
  for (const [index, question] of questions.entries()) {
    const raw = parts[index]?.trim();
    if (!raw) return undefined;
    const numeric = Number.parseInt(raw, 10);
    const option = /^\d+$/.test(raw) && numeric >= 1
      ? question.options[numeric - 1]
      : question.options.find(
          (candidate) => candidate.label.toLocaleLowerCase("zh-CN") ===
            raw.toLocaleLowerCase("zh-CN"),
        );
    answers[question.id] = truncate(option?.label ?? raw, 1_000);
  }
  return answers;
}

function inputAnswerUsage(questions: UserInputQuestion[]): string {
  return questions.length === 1
    ? "请引用问题卡片，回复选项编号、选项文字或自定义答案。"
    : `需要按顺序提供 ${questions.length} 个答案，并用中文分号“；”分隔，例如：1；2；自定义答案。`;
}

function activityEventFromPayload(payload: ActivityHookPayload): ActivityCardEvent {
  const at = new Date().toISOString();
  switch (payload.hook_event_name) {
    case "PreToolUse":
      return {
        at,
        label: `正在调用 ${humanizeToolName(payload.tool_name)}`,
        detail: payload.tool_preview,
      };
    case "PostToolUse":
      return {
        at,
        label: `${humanizeToolName(payload.tool_name)} 已完成`,
        detail: payload.tool_response_preview,
      };
    case "PreCompact":
      return { at, label: "正在压缩上下文" };
    case "PostCompact":
      return { at, label: "上下文压缩完成" };
    case "UserPromptSubmit":
      return { at, label: `已提交新任务，${runtimeDisplayName(payload.runtime)} 开始处理` };
  }
}

function normalizePromptForMatch(value: string): string {
  return value.normalize("NFC").replace(/\s+/gu, " ").trim();
}

function externalSessionInputBlockedMessage(session: SessionRecord): string {
  return notReceivedText(session, "外部会话不支持飞书输入。请回到原窗口继续。");
}

function codexNotReceived(reason: string): string {
  return notReceivedText(undefined, reason);
}

function receivedText(
  session: { runtime?: RuntimeName } | undefined,
): string {
  return runtimeReceivedText(session?.runtime);
}

function notReceivedText(
  session: { runtime?: RuntimeName } | undefined,
  reason: string,
): string {
  return `${runtimeDisplayName(session?.runtime)} 未接收：${reason}`;
}

function isRetryableCodexError(value: string, errorCode?: string): boolean {
  if (
    errorCode &&
    /(?:internal.server|server.error|rate.limit|overload|high.demand|temporar|timeout)/i.test(errorCode)
  ) {
    return true;
  }
  return /(?:\b(?:400|408|409|429|500|502|503|504)\b|too many requests|rate.?limit|busy|overload|temporar(?:y|ily)|service unavailable|timeout|timed out|连接超时|服务繁忙|请求过多|暂时不可用)/i.test(
    value,
  );
}

function codexErrorFromMessage(value: string | null | undefined): string | undefined {
  const message = value?.trim();
  if (!message) {
    return undefined;
  }

  // Stop currently exposes only the last assistant text, not Codex's
  // structured task_complete error. A normal answer may legitimately discuss
  // “400”, “错误” or “失败”, so never scan an entire prose response for a pair
  // of loose keywords. Only accept an explicitly error-shaped first line.
  const firstLine = message
    .split(/\r?\n/u)
    .map((line) => line.trim())
    .find(Boolean);
  if (!firstLine || Array.from(firstLine).length > 500) {
    return undefined;
  }

  const startsLikeError = /^(?:error\b|failed\b|failure\b|exception\b|unable\b|request failed\b|unexpected status\b|exceeded retry limit\b|(?:错误|失败|异常|服务繁忙|请求过多|连接超时|暂时不可用)(?:\s*[:：]|\s|$))/iu.test(
    firstLine,
  );
  const startsWithRetryableStatus = /^(?:http\s*)?(?:400|408|409|429|500|502|503|504)(?:\s*[:：-]\s*|\s+(?:bad\b|too many\b|internal\b|service\b|request\b|gateway\b|error\b|错误|失败|异常))/iu.test(
    firstLine,
  );
  const knownServiceFailure = /^(?:we(?:'re| are) currently experiencing high demand\b|too many requests\b|service unavailable\b|rate.?limit(?:ed| exceeded)?\b|request timed out\b|timed out\b)/iu.test(
    firstLine,
  );
  if (
    !(startsLikeError || startsWithRetryableStatus || knownServiceFailure) ||
    !isRetryableCodexError(firstLine)
  ) {
    return undefined;
  }
  return message;
}

function retryDelayMs(
  settings: BridgeSettings,
  testDelayMs: number | undefined,
): number {
  if (testDelayMs !== undefined) {
    return Math.max(1, testDelayMs);
  }
  const jitter = settings.retryJitterSeconds > 0
    ? Math.floor(Math.random() * (settings.retryJitterSeconds + 1))
    : 0;
  return (settings.retryIntervalSeconds + jitter) * 1_000;
}

function humanizeToolName(toolName: string | undefined): string {
  if (!toolName) return "工具";
  const known: Record<string, string> = {
    shell_command: "命令行",
    apply_patch: "文件修改",
    view_image: "图片查看",
    request_user_input: "用户提问",
  };
  return known[toolName] ?? toolName.replace(/^mcp__/, "MCP · ");
}

function parseExplicitSession(
  text: string,
): { kind: "short"; token: string; prompt: string } | undefined {
  const match = text.match(/^#([a-zA-Z0-9]{4,32})\s+([\s\S]+)$/);
  if (!match?.[1] || !match[2]?.trim()) {
    return undefined;
  }
  return { kind: "short", token: match[1].toLowerCase(), prompt: match[2].trim() };
}

function parseExplicitAlias(
  text: string,
): { kind: "alias"; token: string; prompt: string } | undefined {
  const match = text.match(/^@([^\s@#]+)\s+([\s\S]+)$/u);
  if (!match?.[1] || !match[2]?.trim()) {
    return undefined;
  }
  return { kind: "alias", token: match[1], prompt: match[2].trim() };
}

function parseAliasCommand(text: string): AliasCommand | undefined {
  if (text === "别名") {
    return {};
  }
  const match = text.match(/^别名\s+([#@])([^\s#@]+)(?:\s+([\s\S]+))?$/u);
  if (!match?.[1] || !match[2]) {
    return undefined;
  }
  if (match[1] === "#" && !/^[a-zA-Z0-9]{4,32}$/.test(match[2])) {
    return undefined;
  }
  return {
    targetKind: match[1] === "#" ? "short" : "alias",
    target: match[1] === "#" ? match[2].toLowerCase() : match[2],
    alias: match[3]?.trim(),
  };
}

function aliasCommandUsage(): string {
  return "设置：别名 #短ID 名称\n清除：别名 #短ID 清除\n也可用旧别名定位：别名 @旧别名 新名称\n回复：@名称 你的内容\n规则：1–20 个字符，可用中文、字母、数字、下划线和短横线。";
}

function formatDuration(milliseconds: number): string {
  const hours = milliseconds / (60 * 60 * 1000);
  if (Number.isInteger(hours)) {
    return `${hours} 小时`;
  }
  const minutes = Math.max(1, Math.round(milliseconds / (60 * 1000)));
  return `${minutes} 分钟`;
}

function normalizeActionValue(value: unknown): Record<string, unknown> | undefined {
  if (value && typeof value === "object" && !Array.isArray(value)) {
    return value as Record<string, unknown>;
  }
  if (typeof value === "string") {
    try {
      const parsed: unknown = JSON.parse(value);
      return parsed && typeof parsed === "object" && !Array.isArray(parsed)
        ? (parsed as Record<string, unknown>)
        : undefined;
    } catch {
      return undefined;
    }
  }
  return undefined;
}

function actionToResolution(action: unknown): ApprovalResolution | undefined {
  switch (action) {
    case "approval_allow":
      return "allow";
    case "approval_deny":
      return "deny";
    default:
      return undefined;
  }
}

function approvalResolutionFromText(text: string): ApprovalResolution | undefined {
  const normalized = text.replace(/[\s，。！!]/g, "").toLowerCase();
  if (["批准", "允许", "同意", "approve", "allow"].includes(normalized)) {
    return "allow";
  }
  if (["拒绝", "不允许", "deny", "reject"].includes(normalized)) {
    return "deny";
  }
  return undefined;
}

function approvalText(
  resolution: ApprovalResolution,
  session?: { runtime?: RuntimeName },
): string {
  const runtime = runtimeDisplayName(session?.runtime);
  switch (resolution) {
    case "allow":
      return `已批准，${runtime} 将继续执行。`;
    case "deny":
      return "已拒绝这次操作。";
    case "local":
      return `已转回电脑端，请在原 ${runtime} 窗口确认。`;
    case "timeout":
      return "审批已超时，已转回电脑端。";
  }
}

function sessionGroupActivityTime(session: SessionRecord): number {
  return Math.max(
    parseTimestamp(session.lastSeenAt),
    parseTimestamp(session.feishuChatCreatedAt),
  );
}

function parseTimestamp(value: string | undefined): number {
  const parsed = value ? Date.parse(value) : Number.NaN;
  return Number.isFinite(parsed) ? parsed : 0;
}

function normalizeRuntimeCwd(cwd: string): string {
  const resolved = path.resolve(cwd);
  return process.platform === "win32" ? resolved.toLowerCase() : resolved;
}
