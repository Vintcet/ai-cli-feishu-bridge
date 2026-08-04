import { randomUUID } from "node:crypto";

import { ActivityCoordinator } from "./activity-coordinator.js";
import { ApprovalCoordinator } from "./approval-coordinator.js";
import { assessApprovalRisk } from "./approval-risk.js";
import { buildStopCards, buildUserPromptCards } from "./cards.js";
import { readLastClaudeAssistantMessage } from "./claude-code-transcript.js";
import { readCodexTurnCompletion } from "./codex-transcript.js";
import { CodexTranscriptMonitor } from "./codex-transcript-monitor.js";
import {
  previewJson,
  runtimeDisplayName,
  type ActivityHookPayload,
  type ApprovalRecord,
  type BridgeSettings,
  type MessageRouteKind,
  type PermissionHookPayload,
  type RequestUserInputHookPayload,
  type SessionRecord,
  type SessionEndHookPayload,
  type SessionStartHookPayload,
  type StopHookPayload,
} from "./domain.js";
import { FeishuGateway } from "./feishu.js";
import { FileTransferCoordinator } from "./file-transfer-coordinator.js";
import { extractBridgeFileDirectives } from "./file-transfer.js";
import { ManagedTerminalRouter } from "./managed-terminal.js";
import { codexErrorFromMessage } from "./runtime-errors.js";
import { RuntimeLaunchCoordinator } from "./runtime-launch-coordinator.js";
import { RuntimeRetryCoordinator } from "./runtime-retry-coordinator.js";
import { SessionGroupCoordinator } from "./session-group-coordinator.js";
import { BridgeStore } from "./store.js";
import {
  TurnNotificationCoordinator,
  turnNotificationWasSent,
} from "./turn-notification-coordinator.js";
import { UserInputCoordinator } from "./user-input-coordinator.js";

interface HookEventCoordinatorDependencies {
  store: BridgeStore;
  feishu: FeishuGateway;
  managedTerminals: ManagedTerminalRouter;
  transcriptMonitor: CodexTranscriptMonitor;
  files: FileTransferCoordinator;
  activities: ActivityCoordinator;
  sessionGroups: SessionGroupCoordinator;
  runtimeLaunches: RuntimeLaunchCoordinator;
  approvals: ApprovalCoordinator;
  inputs: UserInputCoordinator;
  runtimeRetries: RuntimeRetryCoordinator;
  turnNotifications: TurnNotificationCoordinator;
  approvalTimeoutMs: number;
  inputTimeoutMs: number;
  isClosing: () => boolean;
  watchTranscript: (session: SessionRecord) => Promise<void>;
  migratePromptState: (oldSessionId: string, newSessionId: string) => void;
  clearPromptState: (sessionId: string) => void;
  prepareStop: (sessionId: string) => void;
  decrementManagedQueueDepth: (sessionId: string) => void;
  drainExternalQueue: (sessionId: string) => Promise<void>;
  consumeRemotePrompt: (sessionId: string, prompt: string) => boolean;
  addRoute: (
    messageId: string,
    sessionId: string,
    chatId: string,
    kind: MessageRouteKind,
  ) => Promise<void>;
}

export class HookEventCoordinator {
  constructor(private readonly dependencies: HookEventCoordinatorDependencies) {}

  async handleSessionStart(
    payload: SessionStartHookPayload,
  ): Promise<Record<string, unknown>> {
    const { managedTerminals, store } = this.dependencies;
    const claimedTerminal = payload.managed_terminal_id
      ? managedTerminals.claimById(
          payload.managed_terminal_id,
          payload.cwd,
          payload.session_id,
        )
      : managedTerminals.claim(payload.cwd, payload.session_id);
    const managedTerminalId =
      payload.managed_terminal_id ?? claimedTerminal?.terminalId ?? null;
    const managedTerminalElevated = managedTerminalId
      ? payload.managed_terminal_elevated ?? claimedTerminal?.elevated ?? null
      : null;
    const placeholder = managedTerminalId
      ? store.findSessionByManagedTerminalId(managedTerminalId)
      : undefined;
    const openedAt =
      placeholder?.openedAt ??
      (claimedTerminal
        ? new Date(claimedTerminal.createdAt).toISOString()
        : undefined);
    const session = await store.upsertSession({
      sessionId: payload.session_id,
      alias:
        placeholder?.source === "managed_window" ? placeholder.alias : undefined,
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
      managedByAssistant: managedTerminalId ? true : false,
      historyEligible: true,
      transcriptPath: payload.transcript_path,
      openedAt,
    });
    if (
      placeholder?.source === "managed_window" &&
      placeholder.sessionId !== session.sessionId
    ) {
      this.dependencies.migratePromptState(
        placeholder.sessionId,
        session.sessionId,
      );
      this.dependencies.files.rekeySession(
        placeholder.sessionId,
        session.sessionId,
      );
      this.dependencies.activities.rekey(
        placeholder.sessionId,
        session.sessionId,
      );
      await store.replaceSessionReferences(
        placeholder.sessionId,
        session.sessionId,
      );
    }
    const currentSession = store.getSession(session.sessionId) ?? session;
    await this.dependencies.watchTranscript(currentSession);
    console.log(
      `[session] ${payload.source} registered session #${currentSession.shortId}.`,
    );
    if (
      currentSession.managedByAssistant === true &&
      !currentSession.feishuChatId
    ) {
      void this.dependencies.sessionGroups.ensure(currentSession.sessionId);
    }
    await this.dependencies.runtimeLaunches.drain(currentSession.sessionId);
    return {};
  }

  async handleSessionEnd(
    payload: SessionEndHookPayload,
  ): Promise<Record<string, unknown>> {
    const { inputs, approvals, transcriptMonitor, store } = this.dependencies;
    await inputs.resolveForSession(payload.session_id, "local");
    await approvals.resolveForSession(payload.session_id);
    await transcriptMonitor.unwatch(payload.session_id);
    const session = await store.upsertSession({
      sessionId: payload.session_id,
      cwd: payload.cwd,
      status: "ended",
      runtime: payload.runtime,
      historyEligible: true,
      transcriptPath: payload.transcript_path,
      ...(payload.managed_terminal_id !== undefined
        ? { managedTerminalId: payload.managed_terminal_id }
        : {}),
      ...(payload.managed_terminal_elevated !== undefined
        ? { managedTerminalElevated: payload.managed_terminal_elevated }
        : {}),
    });
    this.dependencies.clearPromptState(payload.session_id);
    this.dependencies.files.removeSession(payload.session_id);
    this.dependencies.runtimeRetries.reset(payload.session_id);
    void this.dependencies.activities.finish(payload.session_id, "会话已结束");
    this.dependencies.managedTerminals.release(payload.session_id);
    console.log(`[session] Ended session #${session.shortId}.`);
    return {};
  }

  async handlePermission(
    payload: PermissionHookPayload,
    signal?: AbortSignal,
  ): Promise<Record<string, unknown>> {
    if (this.dependencies.isClosing()) {
      return {};
    }
    const { store, approvals } = this.dependencies;
    const session = await store.upsertSession({
      sessionId: payload.session_id,
      cwd: payload.cwd,
      model: payload.model,
      turnId: payload.turn_id,
      status: "pending_approval",
      runtime: payload.runtime,
      transcriptPath: payload.transcript_path,
      ...(payload.managed_terminal_id !== undefined
        ? { managedTerminalId: payload.managed_terminal_id }
        : {}),
      ...(payload.managed_terminal_elevated !== undefined
        ? { managedTerminalElevated: payload.managed_terminal_elevated }
        : {}),
    });

    const now = Date.now();
    const risk = assessApprovalRisk({
      toolName: payload.tool_name,
      toolInput: payload.tool_input,
      cwd: payload.cwd,
    });
    const approval: ApprovalRecord = {
      requestId: randomUUID(),
      sessionId: payload.session_id,
      turnId: payload.turn_id,
      ...(payload.tool_use_id ? { toolUseId: payload.tool_use_id } : {}),
      cwd: payload.cwd,
      toolName: payload.tool_name,
      toolPreview: previewJson(payload.tool_input),
      createdAt: new Date(now).toISOString(),
      expiresAt: new Date(
        now + this.dependencies.approvalTimeoutMs,
      ).toISOString(),
      status: "pending",
      messageIds: [],
      requiresManualApproval:
        !store.getSettings().autoApprove || risk.level === "high",
      desktopApprovalRequested: false,
      riskLevel: risk.level,
      riskReason: risk.reason,
    };
    await this.dependencies.watchTranscript(session);
    if (this.dependencies.isClosing()) {
      await store.upsertSession({
        sessionId: payload.session_id,
        cwd: payload.cwd,
        turnId: payload.turn_id,
        status: "local_approval",
      });
      return {};
    }
    await store.createApproval(approval);
    if (this.dependencies.isClosing()) {
      await approvals.complete(approval.requestId, "local", {
        source: "shutdown",
      });
      return {};
    }
    approvals.logEvent(
      "requested",
      session,
      approval,
      {
        autoApprove: store.getSettings().autoApprove,
        riskLevel: risk.level,
        riskReason: risk.reason,
      },
      now,
    );

    const resultPromise = approvals.createWaiter(approval);
    const handleHookDisconnect = (): void => {
      void approvals.complete(approval.requestId, "local", {
        source: "hook_disconnected",
      });
    };
    signal?.addEventListener("abort", handleHookDisconnect, { once: true });
    try {
      if (signal?.aborted) {
        await approvals.complete(approval.requestId, "local", {
          source: "hook_disconnected",
        });
      } else {
        const automaticallyHandled = await approvals.tryAutomatic(
          session,
          approval,
          payload.runtime ?? "codex",
        );
        if (!automaticallyHandled) {
          const sentCount = await approvals.sendCards(
            session,
            approval,
            "pending",
            "approval",
          );
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
    } finally {
      signal?.removeEventListener("abort", handleHookDisconnect);
    }
  }

  async handleRequestUserInput(
    payload: RequestUserInputHookPayload,
  ): Promise<Record<string, unknown>> {
    if (this.dependencies.isClosing()) {
      return {};
    }
    const { store, inputs } = this.dependencies;
    const session = await store.upsertSession({
      sessionId: payload.session_id,
      cwd: payload.cwd,
      model: payload.model,
      turnId: payload.turn_id,
      status: "pending_input",
      runtime: payload.runtime,
      ...(payload.transcript_path !== undefined
        ? { transcriptPath: payload.transcript_path }
        : {}),
      ...(payload.managed_terminal_id !== undefined
        ? { managedTerminalId: payload.managed_terminal_id }
        : {}),
      ...(payload.managed_terminal_elevated !== undefined
        ? { managedTerminalElevated: payload.managed_terminal_elevated }
        : {}),
    });
    await this.dependencies.watchTranscript(session);
    const recipients = await this.dependencies.sessionGroups
      .notificationRecipients(session);
    if (recipients.length === 0 || this.dependencies.isClosing()) {
      if (this.dependencies.isClosing()) {
        await store.upsertSession({
          sessionId: payload.session_id,
          cwd: payload.cwd,
          turnId: payload.turn_id,
          status: "waiting",
        });
      }
      return {};
    }

    const requestId = randomUUID();
    const autoResolutionMs = payload.tool_input.autoResolutionMs;
    const timeoutMs =
      typeof autoResolutionMs === "number" && autoResolutionMs > 0
        ? Math.min(this.dependencies.inputTimeoutMs, autoResolutionMs)
        : this.dependencies.inputTimeoutMs;
    const resultPromise = inputs.createHookWaiter(requestId, {
      sessionId: payload.session_id,
      turnId: payload.turn_id,
      cwd: payload.cwd,
      questions: payload.tool_input.questions,
      timeoutMs,
    });

    const allQuestionsDelivered = await inputs.sendCards(
      session,
      requestId,
      payload.tool_input.questions,
      recipients,
      "input",
    );
    if (!allQuestionsDelivered) {
      await inputs.complete(requestId, { kind: "local" });
    }

    const resolution = await resultPromise;
    if (resolution.kind !== "answered") {
      return {};
    }
    const answerText = payload.tool_input.questions
      .map(
        (question, index) =>
          `${index + 1}. ${question.header} (${question.id}): ${(resolution.answers[question.id] ?? []).join("、")}`,
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
              ? [[questionText, (resolution.answers[question.id] ?? []).join(", ")]]
              : [];
          }),
        );
        const annotations = Object.fromEntries(
          payload.tool_input.questions.flatMap((question) => {
            const questionText = questionTextById[question.id];
            const selected = resolution.answers[question.id] ?? [];
            if (!questionText || selected.length !== 1) {
              return [];
            }
            const preview = question.options.find(
              (option) => option.label === selected[0],
            )?.preview;
            return preview ? [[questionText, { preview }]] : [];
          }),
        );
        return {
          hookSpecificOutput: {
            hookEventName: "PreToolUse",
            permissionDecision: "allow",
            updatedInput: {
              ...originalInput,
              answers,
              annotations,
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

  async handleActivity(
    payload: ActivityHookPayload,
  ): Promise<Record<string, unknown>> {
    if (
      payload.runtime === "claudecode" &&
      payload.hook_event_name === "PreToolUse"
    ) {
      const matchingApprovals = this.dependencies.store
        .listApprovals()
        .filter(
          (approval) =>
            approval.status === "pending" &&
            approval.sessionId === payload.session_id &&
            (payload.tool_use_id
              ? approval.toolUseId === payload.tool_use_id ||
                (!approval.toolUseId && approval.turnId === payload.turn_id)
              : approval.turnId === payload.turn_id),
        );
      for (const approval of matchingApprovals) {
        await this.dependencies.approvals.complete(
          approval.requestId,
          "allow",
          { source: "claudecode_runtime" },
        );
      }
    }
    const settings = this.dependencies.store.getSettings();
    if (payload.hook_event_name === "UserPromptSubmit") {
      void this.handleUserPromptSubmit(payload, settings).catch((error) => {
        console.error("[prompt] Could not sync the PC prompt to Feishu:", error);
      });
    }
    if (settings.notifyActivity) {
      void this.dependencies.activities.record(payload).catch((error) => {
        console.error("[activity] Could not record Codex activity:", error);
      });
    }
    return {};
  }

  async handleStop(payload: StopHookPayload): Promise<Record<string, unknown>> {
    const { store, runtimeRetries, activities, files, turnNotifications } =
      this.dependencies;
    this.dependencies.prepareStop(payload.session_id);
    const previous = store.getSession(payload.session_id);

    let assistantMessage = payload.last_assistant_message;
    let turnId = payload.turn_id;
    let structuredCodexError: string | undefined;
    let structuredCodexErrorCode: string | undefined;
    if (payload.runtime === "claudecode" && payload.transcript_path) {
      const transcriptMessage = await readLastClaudeAssistantMessage(
        payload.transcript_path,
      );
      assistantMessage ||= transcriptMessage?.text ?? null;
      turnId = transcriptMessage?.turnId ?? turnId;
    } else if (payload.transcript_path) {
      const completion = await readCodexTurnCompletion(
        payload.transcript_path,
        turnId,
      );
      assistantMessage ||= completion?.assistantMessage ?? null;
      structuredCodexError = completion?.error;
      structuredCodexErrorCode = completion?.errorCode;
      turnId = completion?.turnId ?? turnId;
    }

    const session = await store.upsertSession({
      sessionId: payload.session_id,
      cwd: payload.cwd,
      model: payload.model,
      turnId,
      status: "waiting",
      assistantMessage,
      runtime: payload.runtime,
      transcriptPath: payload.transcript_path,
      ...(payload.managed_terminal_id !== undefined
        ? { managedTerminalId: payload.managed_terminal_id }
        : {}),
      ...(payload.managed_terminal_elevated !== undefined
        ? { managedTerminalElevated: payload.managed_terminal_elevated }
        : {}),
    });
    const codexError =
      structuredCodexError ?? codexErrorFromMessage(assistantMessage);
    if (!codexError) {
      runtimeRetries.reset(payload.session_id);
    }
    if (turnId && turnNotificationWasSent(previous, turnId)) {
      return {};
    }
    if (codexError) {
      await runtimeRetries.notifyTurnError(
        session,
        turnId,
        codexError,
        structuredCodexErrorCode,
      );
      return {};
    }
    await activities.finish(payload.session_id, "本轮处理完成");
    const fileReturnRequest = files.advanceReturnRequests(payload.session_id);
    this.dependencies.decrementManagedQueueDepth(payload.session_id);

    const fileDirectives = extractBridgeFileDirectives(
      assistantMessage?.trim() ||
        `${runtimeDisplayName(session.runtime)} 已结束本轮处理。`,
    );
    const message =
      fileDirectives.displayMessage ||
      `${runtimeDisplayName(session.runtime)} 已结束本轮处理。`;
    const { sentCount } = await turnNotifications.send(
      session,
      turnId,
      "stop",
      message,
      buildStopCards(session, message),
      "[stop] Failed to send a Feishu completion card:",
    );
    if (sentCount > 0) {
      console.log(`[stop] Notified Feishu for session #${session.shortId}.`);
    }
    if (fileReturnRequest && fileDirectives.paths.length > 0) {
      void files
        .sendRequestedFiles(
          session,
          fileReturnRequest.chatId,
          fileDirectives.paths,
        )
        .catch((error) => {
          console.error("[files] Asynchronous file return failed:", error);
        });
    }
    void this.dependencies.drainExternalQueue(payload.session_id);
    return {};
  }

  private async handleUserPromptSubmit(
    payload: ActivityHookPayload,
    settings: BridgeSettings,
  ): Promise<void> {
    const prompt = payload.prompt;
    if (!prompt?.trim()) {
      return;
    }
    if (this.dependencies.consumeRemotePrompt(payload.session_id, prompt)) {
      return;
    }
    await this.dependencies.runtimeRetries.beginManualTurn(payload.session_id);
    if (!settings.notifyUserPrompts) {
      return;
    }
    const session = this.dependencies.store.getSession(payload.session_id);
    if (session?.managedByAssistant !== true) {
      return;
    }
    for (
      const recipient of await this.dependencies.sessionGroups
        .notificationRecipients(session)
    ) {
      try {
        for (const card of buildUserPromptCards(session, prompt)) {
          const messageId = await this.dependencies.feishu.sendCard(
            recipient.chatId,
            card,
          );
          await this.dependencies.addRoute(
            messageId,
            session.sessionId,
            recipient.chatId,
            "user_prompt",
          );
        }
      } catch (error) {
        console.error("[prompt] Failed to send a PC prompt card:", error);
      }
    }
  }
}
