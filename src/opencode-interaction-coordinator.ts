import { randomUUID } from "node:crypto";

import { ApprovalCoordinator } from "./approval-coordinator.js";
import { assessApprovalRisk } from "./approval-risk.js";
import {
  previewJson,
  type ApprovalRecord,
  type UserInputAnswers,
  type UserInputQuestion,
} from "./domain.js";
import type {
  OpenCodePermission,
  OpenCodePermissionReplied,
  OpenCodeQuestionRejected,
  OpenCodeQuestionReplied,
  OpenCodeQuestionRequest,
} from "./opencode-client.js";
import { OpenCodeManager } from "./opencode-manager.js";
import { SessionGroupCoordinator } from "./session-group-coordinator.js";
import { BridgeStore } from "./store.js";
import { UserInputCoordinator } from "./user-input-coordinator.js";

interface OpenCodeInteractionCoordinatorDependencies {
  store: BridgeStore;
  opencode?: OpenCodeManager;
  approvals: ApprovalCoordinator;
  inputs: UserInputCoordinator;
  sessionGroups: SessionGroupCoordinator;
  approvalTimeoutMs: number;
  isClosing: () => boolean;
}

export class OpenCodeInteractionCoordinator {
  private readonly permissionClaims = new Set<string>();
  private readonly questionClaims = new Set<string>();

  constructor(
    private readonly dependencies: OpenCodeInteractionCoordinatorDependencies,
  ) {}

  releasePermissionClaim(sessionId: string, permissionId: string): void {
    this.permissionClaims.delete(this.interactionKey(sessionId, permissionId));
  }

  releaseQuestionClaim(sessionId: string, requestId: string): void {
    this.questionClaims.delete(this.interactionKey(sessionId, requestId));
  }

  clearSession(sessionId: string): void {
    const prefix = `${sessionId}\u0000`;
    for (const key of this.permissionClaims) {
      if (key.startsWith(prefix)) {
        this.permissionClaims.delete(key);
      }
    }
    for (const key of this.questionClaims) {
      if (key.startsWith(prefix)) {
        this.questionClaims.delete(key);
      }
    }
  }

  async handlePermissionUpdated(permission: OpenCodePermission): Promise<void> {
    if (this.dependencies.isClosing()) {
      return;
    }
    const sessionId = permission.sessionID;
    if (!sessionId) {
      return;
    }
    const claimKey = this.interactionKey(sessionId, permission.id);
    if (this.permissionClaims.has(claimKey)) {
      return;
    }
    this.permissionClaims.add(claimKey);
    try {
      const { store, opencode, approvals } = this.dependencies;
      const existing = store.listApprovals().find(
        (approval) =>
          approval.sessionId === sessionId &&
          approval.opencodePermissionId === permission.id,
      );
      if (existing && existing.status !== "pending") {
        return;
      }
      const current = store.getSession(sessionId);
      const instance = opencode?.findInstanceBySession(sessionId);
      const cwd = current?.cwd || existing?.cwd || instance?.cwd || "";
      const session = await store.upsertSession({
        sessionId,
        cwd,
        model: current?.model,
        status: "pending_approval",
        runtime: "opencode",
        managedByAssistant: true,
      });
      if (this.dependencies.isClosing()) {
        return;
      }
      if (existing) {
        const automaticallyHandled = await approvals.tryAutomatic(
          session,
          existing,
          "opencode",
        );
        if (!automaticallyHandled && existing.messageIds.length === 0) {
          await approvals.sendCards(session, existing, "pending", "opencode");
        }
        return;
      }
      const now = Date.now();
      const toolName =
        permission.action ??
        (typeof permission.permission === "string"
          ? permission.permission
          : permission.type ?? "permission");
      const toolInput = {
        action: permission.action,
        resources: permission.resources,
        save: permission.save,
        source: permission.source,
        permission: permission.permission ?? permission.type,
        patterns: permission.patterns,
        input: permission.input,
        metadata: permission.metadata,
        always: permission.always,
      };
      const risk = assessApprovalRisk({ toolName, toolInput, cwd });
      const approval: ApprovalRecord = {
        requestId: randomUUID(),
        sessionId,
        turnId: current?.lastTurnId ?? `opencode-${now}`,
        cwd,
        toolName,
        toolPreview: previewJson(toolInput),
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
        opencodePermissionId: permission.id,
      };
      await store.createApproval(approval);
      if (this.dependencies.isClosing()) {
        await approvals.complete(approval.requestId, "local", {
          source: "shutdown",
        });
        return;
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
      approvals.scheduleTimeout(approval);

      const automaticallyHandled = await approvals.tryAutomatic(
        session,
        approval,
        "opencode",
      );
      if (!automaticallyHandled) {
        await approvals.sendCards(session, approval, "pending", "opencode");
      }
    } finally {
      this.permissionClaims.delete(claimKey);
    }
  }

  async handlePermissionReplied(reply: OpenCodePermissionReplied): Promise<void> {
    const claimKey = this.interactionKey(reply.sessionID, reply.requestID);
    const approval = this.dependencies.store
      .listApprovals()
      .find(
        (item) =>
          item.sessionId === reply.sessionID &&
          item.opencodePermissionId === reply.requestID &&
          item.status === "pending",
      );
    if (!approval) {
      this.permissionClaims.delete(claimKey);
      if (!this.dependencies.store.hasPendingApprovalForSession(reply.sessionID)) {
        await this.updateStatus(
          reply.sessionID,
          reply.reply === "reject" ? "waiting" : "running",
        );
      }
      return;
    }
    try {
      await this.dependencies.approvals.complete(
        approval.requestId,
        reply.reply === "reject" ? "deny" : "allow",
        {
          source: "opencode_runtime",
          forwardOpenCode: false,
        },
      );
    } finally {
      this.permissionClaims.delete(claimKey);
    }
  }

  async handleQuestionAsked(request: OpenCodeQuestionRequest): Promise<void> {
    if (this.dependencies.isClosing()) {
      return;
    }
    const claimKey = this.interactionKey(request.sessionID, request.id);
    if (this.questionClaims.has(claimKey)) {
      return;
    }
    this.questionClaims.add(claimKey);
    let requestId: string | undefined;
    try {
      const { store, opencode, inputs, sessionGroups } = this.dependencies;
      const current = store.getSession(request.sessionID);
      const instance = opencode?.findInstanceBySession(request.sessionID);
      const cwd = current?.cwd || instance?.cwd || "";
      if (!cwd || request.questions.length === 0) {
        this.questionClaims.delete(claimKey);
        return;
      }
      const questions: UserInputQuestion[] = request.questions.map(
        (question, index) => ({
          header: question.header || `问题 ${index + 1}`,
          id: `opencode_question_${index + 1}`,
          question: question.question,
          options: question.options,
          multiple: question.multiple === true,
          custom: question.custom !== false,
        }),
      );
      const session = await store.upsertSession({
        sessionId: request.sessionID,
        cwd,
        model: current?.model,
        turnId: current?.lastTurnId ?? `opencode-${request.id}`,
        status: "pending_input",
        runtime: "opencode",
        managedByAssistant: true,
      });
      const recipients = await sessionGroups.notificationRecipients(session);
      if (recipients.length === 0 || this.dependencies.isClosing()) {
        return;
      }

      requestId = randomUUID();
      inputs.registerOpenCode(requestId, {
        sessionId: request.sessionID,
        turnId: current?.lastTurnId ?? `opencode-${request.id}`,
        cwd,
        questions,
        opencodeRequestId: request.id,
      });

      const allQuestionsDelivered = await inputs.sendCards(
        session,
        requestId,
        questions,
        recipients,
        "opencode",
      );
      if (!allQuestionsDelivered) {
        await inputs.complete(requestId, { kind: "local" });
      }
    } catch (error) {
      if (requestId) {
        this.dependencies.inputs.discard(requestId);
      }
      this.questionClaims.delete(claimKey);
      throw error;
    }
  }

  async handleQuestionReplied(reply: OpenCodeQuestionReplied): Promise<void> {
    const claimKey = this.interactionKey(reply.sessionID, reply.requestID);
    const pending = this.dependencies.inputs.findOpenCodeInput(
      reply.sessionID,
      reply.requestID,
    );
    if (!pending) {
      this.questionClaims.delete(claimKey);
      await this.updateStatus(reply.sessionID, "running");
      return;
    }
    const answers: UserInputAnswers = {};
    pending.waiter.questions.forEach((question, index) => {
      answers[question.id] = reply.answers[index] ?? [];
    });
    try {
      await this.dependencies.inputs.complete(pending.requestId, {
        kind: "answered",
        answers,
      });
    } finally {
      this.questionClaims.delete(claimKey);
    }
  }

  async handleQuestionRejected(rejection: OpenCodeQuestionRejected): Promise<void> {
    const claimKey = this.interactionKey(
      rejection.sessionID,
      rejection.requestID,
    );
    const pending = this.dependencies.inputs.findOpenCodeInput(
      rejection.sessionID,
      rejection.requestID,
    );
    if (pending) {
      try {
        await this.dependencies.inputs.complete(pending.requestId, {
          kind: "rejected",
        });
      } finally {
        this.questionClaims.delete(claimKey);
      }
      return;
    }
    this.questionClaims.delete(claimKey);
    await this.updateStatus(rejection.sessionID, "waiting");
  }

  private interactionKey(sessionId: string, requestId: string): string {
    return `${sessionId}\u0000${requestId}`;
  }

  private async updateStatus(
    sessionId: string,
    status: "running" | "waiting",
  ): Promise<void> {
    const { store, opencode, inputs } = this.dependencies;
    const current = store.getSession(sessionId);
    const instance = opencode?.findInstanceBySession(sessionId);
    const cwd = current?.cwd || instance?.cwd || "";
    if (
      !cwd ||
      current?.status === "ended" ||
      inputs.hasPendingForSession(sessionId) ||
      store.hasPendingApprovalForSession(sessionId)
    ) {
      return;
    }
    await store.upsertSession({
      sessionId,
      cwd,
      model: current?.model,
      status,
      runtime: "opencode",
      managedByAssistant: true,
    });
  }
}
