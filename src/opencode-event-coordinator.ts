import { randomUUID } from "node:crypto";

import { ActivityCoordinator } from "./activity-coordinator.js";
import { buildStopCards, buildUserPromptCards } from "./cards.js";
import {
  previewJson,
  stringifyModel,
  truncate,
  type ActivityHookPayload,
  type MessageRouteKind,
} from "./domain.js";
import { FeishuGateway } from "./feishu.js";
import { FileTransferCoordinator } from "./file-transfer-coordinator.js";
import { extractBridgeFileDirectives } from "./file-transfer.js";
import type {
  OpenCodeMessage,
  OpenCodeMessagePartUpdatedProperties,
  OpenCodeSession,
} from "./opencode-client.js";
import { OpenCodeManager } from "./opencode-manager.js";
import { RuntimeLaunchCoordinator } from "./runtime-launch-coordinator.js";
import { RuntimeRetryCoordinator } from "./runtime-retry-coordinator.js";
import { SessionGroupCoordinator } from "./session-group-coordinator.js";
import { BridgeStore } from "./store.js";
import { TurnNotificationCoordinator } from "./turn-notification-coordinator.js";
import { UserInputCoordinator } from "./user-input-coordinator.js";

interface OpenCodeEventCoordinatorDependencies {
  store: BridgeStore;
  feishu: FeishuGateway;
  opencode?: OpenCodeManager;
  sessionGroups: SessionGroupCoordinator;
  runtimeLaunches: RuntimeLaunchCoordinator;
  runtimeRetries: RuntimeRetryCoordinator;
  inputs: UserInputCoordinator;
  activities: ActivityCoordinator;
  turnNotifications: TurnNotificationCoordinator;
  files: FileTransferCoordinator;
  releaseRemoteInputLock: (sessionId: string) => void;
  drainQueue: (sessionId: string) => Promise<void>;
  forgetSession: (sessionId: string, reason: string) => Promise<void>;
  consumeRemotePrompt: (sessionId: string, prompt: string) => boolean;
  addRoute: (
    messageId: string,
    sessionId: string,
    chatId: string,
    kind: MessageRouteKind,
  ) => Promise<void>;
}

export class OpenCodeEventCoordinator {
  private readonly portSessions = new Map<number, Set<string>>();
  private readonly toolParts = new Map<string, Map<string, string>>();

  constructor(
    private readonly dependencies: OpenCodeEventCoordinatorDependencies,
  ) {}

  clearSession(sessionId: string): void {
    this.toolParts.delete(sessionId);
    for (const [port, sessionIds] of this.portSessions) {
      sessionIds.delete(sessionId);
      if (sessionIds.size === 0) {
        this.portSessions.delete(port);
      }
    }
  }

  async handleSessionCreated(session: OpenCodeSession): Promise<void> {
    const sessionId = session.id;
    const instance = this.dependencies.opencode?.findInstanceBySession(sessionId);
    const cwd = session.directory || instance?.cwd || "";
    if (!cwd) {
      return;
    }
    const existing = this.dependencies.store.getSession(sessionId);
    const record = await this.dependencies.store.upsertSession({
      sessionId,
      cwd,
      model: stringifyModel(session.model),
      status:
        existing?.status === "ended"
          ? "waiting"
          : existing?.status ?? "waiting",
      source: "opencode",
      runtime: "opencode",
      managedByAssistant: true,
    });
    const port = instance?.port;
    if (port !== undefined) {
      let sessionIds = this.portSessions.get(port);
      if (!sessionIds) {
        sessionIds = new Set();
        this.portSessions.set(port, sessionIds);
      }
      sessionIds.add(sessionId);
    }
    console.log(`[opencode] Registered session #${record.shortId} (${cwd}).`);
    if (!record.feishuChatId) {
      void this.dependencies.sessionGroups.ensure(sessionId).catch((error) => {
        console.warn("[opencode] Could not create Feishu group:", error);
      });
    }
    await this.dependencies.runtimeLaunches.drain(sessionId);
  }

  async handleSessionDeleted(sessionId: string): Promise<void> {
    await this.dependencies.forgetSession(sessionId, "会话已关闭");
  }

  async handleSessionStatus(sessionId: string, status: string): Promise<void> {
    const { store, inputs } = this.dependencies;
    const current = store.getSession(sessionId);
    if (!current || current.runtime !== "opencode" || current.status === "ended") {
      return;
    }
    if (
      status === "busy" &&
      !inputs.hasPendingForSession(sessionId) &&
      !store.hasPendingApprovalForSession(sessionId)
    ) {
      await store.upsertSession({
        sessionId,
        cwd: current.cwd,
        model: current.model,
        status: "running",
        runtime: "opencode",
        managedByAssistant: true,
      });
    }
  }

  async handleSessionCompacted(sessionId: string): Promise<void> {
    const current = this.dependencies.store.getSession(sessionId);
    if (!current || current.runtime !== "opencode") {
      return;
    }
    if (this.dependencies.store.getSettings().notifyActivity) {
      await this.recordActivity(sessionId, {
        hook_event_name: "PreCompact",
        cwd: current.cwd,
      });
    }
  }

  async handleSessionIdle(sessionId: string): Promise<void> {
    const {
      store,
      opencode,
      inputs,
      runtimeRetries,
      activities,
      turnNotifications,
      files,
    } = this.dependencies;
    const current = store.getSession(sessionId);
    if (!current || current.runtime !== "opencode" || current.status === "ended") {
      this.dependencies.releaseRemoteInputLock(sessionId);
      return;
    }
    if (
      inputs.hasPendingForSession(sessionId) ||
      store.hasPendingApprovalForSession(sessionId)
    ) {
      return;
    }
    const result = await opencode?.lastAssistantText(sessionId);
    const assistantMessage = result?.text || undefined;
    const hasError = result?.hasError === true;
    if (hasError) {
      const detail =
        assistantMessage || current.lastError || "opencode 本轮发生错误。";
      if (current.status === "error") {
        if (!runtimeRetries.hasActiveRetry(sessionId)) {
          this.dependencies.releaseRemoteInputLock(sessionId);
          void this.dependencies.drainQueue(sessionId);
        }
        return;
      }
      const retrying = await runtimeRetries.notifyTurnError(
        current,
        `opencode-error-${randomUUID()}`,
        detail,
      );
      if (!retrying) {
        this.dependencies.releaseRemoteInputLock(sessionId);
        void this.dependencies.drainQueue(sessionId);
      }
      return;
    }
    this.dependencies.releaseRemoteInputLock(sessionId);
    runtimeRetries.reset(sessionId);
    const turnId = `opencode-${Date.now()}`;
    const session = await store.upsertSession({
      sessionId,
      cwd: current.cwd,
      model: current.model,
      turnId,
      status: "waiting",
      assistantMessage,
      runtime: "opencode",
      managedByAssistant: true,
    });
    await activities.finish(sessionId, "本轮处理完成");

    const fileDirectives = extractBridgeFileDirectives(
      assistantMessage?.trim() || "opencode 已结束本轮处理。",
    );
    const message =
      fileDirectives.displayMessage ||
      assistantMessage ||
      "opencode 已结束本轮处理。";
    await turnNotifications.send(
      session,
      turnId,
      "stop",
      message,
      buildStopCards(session, message),
      "[opencode] Failed to send a completion card:",
    );

    const fileReturnRequest = files.advanceReturnRequests(sessionId);
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
    void this.dependencies.drainQueue(sessionId);
  }

  async handleSessionError(
    sessionId: string,
    error: string | undefined,
  ): Promise<void> {
    const current = this.dependencies.store.getSession(sessionId);
    if (!current || current.runtime !== "opencode" || current.status === "ended") {
      return;
    }
    const detail = truncate(error || "opencode 发生未知错误。", 500);
    if (current.status === "error") {
      return;
    }
    const retrying = await this.dependencies.runtimeRetries.notifyTurnError(
      current,
      `opencode-error-${randomUUID()}`,
      detail,
    );
    if (!retrying) {
      this.dependencies.releaseRemoteInputLock(sessionId);
      void this.dependencies.drainQueue(sessionId);
    }
  }

  async handleInstanceDisconnected(port: number): Promise<void> {
    const sessionIds = this.portSessions.get(port);
    if (!sessionIds) {
      return;
    }
    for (const sessionId of [...sessionIds]) {
      await this.dependencies.forgetSession(
        sessionId,
        "opencode 窗口已关闭",
      );
    }
    this.portSessions.delete(port);
  }

  async handleMessagePartUpdated(
    properties: OpenCodeMessagePartUpdatedProperties,
  ): Promise<void> {
    const sessionId = properties.sessionID;
    const part = properties.part;
    if (!sessionId || !part || !this.dependencies.store.getSettings().notifyActivity) {
      return;
    }
    const status = part.state?.status;
    if (!status || part.type !== "tool") {
      return;
    }
    const current = this.dependencies.store.getSession(sessionId);
    if (!current || current.runtime !== "opencode") {
      return;
    }
    let partsBySession = this.toolParts.get(sessionId);
    if (!partsBySession) {
      partsBySession = new Map();
      this.toolParts.set(sessionId, partsBySession);
    }
    const partId =
      part.id ||
      `${properties.messageID ?? "?"}-${part.tool}-${part.state?.title ?? ""}`;
    const previous = partsBySession.get(partId);
    if (previous === status) {
      return;
    }
    partsBySession.set(partId, status);
    const toolName = part.tool;
    if (status === "running" || status === "pending") {
      await this.recordActivity(sessionId, {
        hook_event_name: "PreToolUse",
        cwd: current.cwd,
        tool_name: toolName,
        tool_preview: previewJson(part.state?.input, 800),
      });
    } else if (status === "completed") {
      await this.recordActivity(sessionId, {
        hook_event_name: "PostToolUse",
        cwd: current.cwd,
        tool_name: toolName,
        tool_response_preview: previewJson(part.state?.output, 800),
      });
    }
  }

  async handleMessageUpdated(message: OpenCodeMessage): Promise<void> {
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
    if (this.dependencies.consumeRemotePrompt(sessionId, prompt)) {
      return;
    }
    const settings = this.dependencies.store.getSettings();
    const session = this.dependencies.store.getSession(sessionId);
    if (!settings.notifyUserPrompts || session?.managedByAssistant !== true) {
      return;
    }
    await this.dependencies.store.upsertSession({
      sessionId,
      cwd: session.cwd,
      model: session.model,
      status: "running",
      runtime: "opencode",
      managedByAssistant: true,
    });
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
            sessionId,
            recipient.chatId,
            "user_prompt",
          );
        }
      } catch (error) {
        console.error("[opencode] Failed to send a PC prompt card:", error);
      }
    }
  }

  private async recordActivity(
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
    await this.dependencies.activities.record(payload);
  }
}
