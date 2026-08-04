import { appendFile, rename, rm, stat } from "node:fs/promises";

import {
  buildApprovalCard,
  buildDesktopApprovalCard,
  buildResolvedApprovalCard,
} from "./cards.js";
import type {
  ApprovalRecord,
  ApprovalResolution,
  Binding,
  SessionRecord,
} from "./domain.js";
import {
  projectNameFromCwd,
  runtimeDisplayName,
  sessionLabel,
  shortSessionId,
  truncate,
} from "./domain.js";
import { FeishuGateway } from "./feishu.js";
import { OpenCodeManager } from "./opencode-manager.js";
import { BridgeStore } from "./store.js";

interface ApprovalWaiter {
  timer: NodeJS.Timeout;
  resolve: (resolution: ApprovalResolution) => void;
}

export type ApprovalDecisionSource =
  | "automatic"
  | "desktop"
  | "feishu_card"
  | "feishu_text"
  | "opencode_runtime"
  | "claudecode_runtime"
  | "hook_disconnected"
  | "timeout"
  | "session_closed"
  | "shutdown";

export interface ApprovalCompletionOptions {
  source: ApprovalDecisionSource;
  forwardOpenCode?: boolean;
}

interface ApprovalNotificationTiming {
  firstSentAt: number;
  lastSentAt: number;
  count: number;
}

export interface ApprovalCoordinatorConfig {
  approvalTimeoutMs: number;
  approvalLogPath?: string;
  approvalLogMaxBytes?: number;
  approvalLogMaxBackups?: number;
}

export interface ApprovalRecipient {
  chatId: string;
  binding?: Binding;
}

interface ApprovalCoordinatorDependencies {
  store: BridgeStore;
  feishu: FeishuGateway;
  opencode?: OpenCodeManager;
  config: ApprovalCoordinatorConfig;
  notificationRecipients: (
    session: SessionRecord,
  ) => Promise<ApprovalRecipient[]>;
  onOpenCodePermissionForwarded?: (
    sessionId: string,
    permissionId: string,
  ) => void;
}

export class ApprovalCoordinator {
  private readonly waiters = new Map<string, ApprovalWaiter>();
  private readonly completions = new Map<string, Promise<boolean>>();
  private readonly notificationTimings = new Map<
    string,
    ApprovalNotificationTiming
  >();
  private readonly standaloneTimeouts = new Map<string, NodeJS.Timeout>();
  private logWrite: Promise<void> = Promise.resolve();

  constructor(private readonly dependencies: ApprovalCoordinatorDependencies) {}

  get pendingWaiterCount(): number {
    return this.waiters.size;
  }

  createWaiter(approval: ApprovalRecord): Promise<ApprovalResolution> {
    return new Promise<ApprovalResolution>((resolve) => {
      const timer = setTimeout(() => {
        void this.complete(approval.requestId, "timeout", {
          source: "timeout",
        });
      }, this.dependencies.config.approvalTimeoutMs);
      this.waiters.set(approval.requestId, { timer, resolve });
    });
  }

  scheduleTimeout(approval: ApprovalRecord): void {
    this.clearStandaloneTimeout(approval.requestId);
    const timer = setTimeout(() => {
      this.standaloneTimeouts.delete(approval.requestId);
      void this.complete(approval.requestId, "timeout", {
        source: "timeout",
      });
    }, this.dependencies.config.approvalTimeoutMs);
    timer.unref?.();
    this.standaloneTimeouts.set(approval.requestId, timer);
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
      (resolution !== "allow" && resolution !== "deny")
    ) {
      return { ok: false, error: "审批请求或处理方式不正确。" };
    }

    const existing = this.dependencies.store.getApproval(requestId);
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

    const completed = await this.complete(requestId, resolution, {
      source: "desktop",
    });
    if (!completed) {
      const current = this.dependencies.store.getApproval(requestId);
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
      message: approvalText(
        resolution,
        this.dependencies.store.getSession(existing.sessionId),
      ),
    };
  }

  async tryAutomatic(
    session: SessionRecord,
    approval: ApprovalRecord,
    logPrefix: string,
  ): Promise<boolean> {
    const settings = this.dependencies.store.getSettings();
    if (!settings.autoApprove) {
      return false;
    }
    if (approval.riskLevel !== "low") {
      this.logEvent("automatic_skipped_high_risk", session, approval, {
        path: logPrefix,
        riskLevel: approval.riskLevel ?? "unknown",
        riskReason: approval.riskReason ?? "未记录风险判定",
        elapsedSinceRequestMs: elapsedMs(approval.createdAt),
      });
      return false;
    }
    this.logEvent("automatic_attempt", session, approval, {
      path: logPrefix,
      riskLevel: approval.riskLevel,
      riskReason: approval.riskReason,
      elapsedSinceRequestMs: elapsedMs(approval.createdAt),
    });
    const completed = await this.complete(approval.requestId, "allow", {
      source: "automatic",
    });
    if (completed) {
      this.logEvent("automatic_completed", session, approval, {
        path: logPrefix,
        elapsedSinceRequestMs: elapsedMs(approval.createdAt),
      });
      const resolved = this.dependencies.store.getApproval(approval.requestId);
      if (
        settings.notifyAutoApprovals &&
        resolved?.status === "resolved" &&
        resolved.messageIds.length === 0
      ) {
        await this.sendCards(session, resolved, "resolved", logPrefix);
      }
      return true;
    }
    if (this.dependencies.store.getApproval(approval.requestId)?.status !== "pending") {
      return true;
    }
    this.logEvent("automatic_failed", session, approval, {
      path: logPrefix,
      elapsedSinceRequestMs: elapsedMs(approval.createdAt),
    });
    console.warn(
      `[${logPrefix}] Automatic approval failed for session #${session.shortId}; falling back to manual approval.`,
    );
    return false;
  }

  async sendCards(
    session: SessionRecord,
    approval: ApprovalRecord,
    state: "pending" | "resolved",
    logPrefix: string,
  ): Promise<number> {
    const { store, feishu, notificationRecipients } = this.dependencies;
    if (
      state === "pending" &&
      store.getApproval(approval.requestId)?.status !== "pending"
    ) {
      return 0;
    }
    if (state === "pending") {
      await store.requireManualApproval(approval.requestId);
    }
    let recipients: ApprovalRecipient[];
    try {
      recipients = await notificationRecipients(session);
    } catch (error) {
      this.logEvent("notification_recipients_failed", session, approval, {
        path: logPrefix,
        state,
        error: errorMessage(error),
      });
      console.error(`[${logPrefix}] Failed to resolve approval recipients:`, error);
      if (state === "pending") {
        await this.requestDesktop(
          approval.requestId,
          "notification_unavailable",
        );
      }
      return 0;
    }
    this.logEvent("notification_dispatch", session, approval, {
      path: logPrefix,
      state,
      recipientCount: recipients.length,
      elapsedSinceRequestMs: elapsedMs(approval.createdAt),
    });
    if (recipients.length === 0) {
      this.logEvent("notification_skipped", session, approval, {
        path: logPrefix,
        state,
        reason: "no_recipients",
      });
    }
    let sentCount = 0;
    for (const recipient of recipients) {
      let messageId: string | undefined;
      try {
        const card = state === "pending"
          ? buildApprovalCard(session, approval)
          : buildResolvedApprovalCard(session, approval, "allow");
        messageId = await feishu.sendCard(recipient.chatId, card);
        const sentAt = Date.now();
        sentCount += 1;
        if (state === "pending") {
          const timing = this.notificationTimings.get(approval.requestId);
          this.notificationTimings.set(
            approval.requestId,
            timing
              ? {
                  firstSentAt: timing.firstSentAt,
                  lastSentAt: sentAt,
                  count: timing.count + 1,
                }
              : { firstSentAt: sentAt, lastSentAt: sentAt, count: 1 },
          );
        }
        // Record the external send before local persistence or routing can fail.
        this.logEvent(
          "notification_sent",
          session,
          approval,
          {
            path: logPrefix,
            state,
            chatId: recipient.chatId,
            messageId,
            elapsedSinceRequestMs: elapsedMs(approval.createdAt, sentAt),
          },
          sentAt,
        );
        await store.addApprovalMessage(approval.requestId, messageId);
        await store.addMessageRoute({
          messageId,
          sessionId: approval.sessionId,
          requestId: approval.requestId,
          chatId: recipient.chatId,
          kind: "approval",
          createdAt: new Date().toISOString(),
        });
        const latest = store.getApproval(approval.requestId);
        if (
          state === "pending" &&
          latest?.status === "resolved" &&
          latest.resolution
        ) {
          await feishu.patchCard(
            messageId,
            buildResolvedApprovalCard(session, latest, latest.resolution),
          );
        }
      } catch (error) {
        this.logEvent(
          messageId ? "notification_followup_failed" : "notification_failed",
          session,
          approval,
          {
            path: logPrefix,
            state,
            chatId: recipient.chatId,
            ...(messageId ? { messageId } : {}),
            error: errorMessage(error),
            elapsedSinceRequestMs: elapsedMs(approval.createdAt),
          },
        );
        console.error(
          `[${logPrefix}] ${messageId ? "Failed to finish approval notification handling" : "Failed to send an approval card"}:`,
          error,
        );
      }
    }
    if (state === "pending" && sentCount === 0) {
      await this.requestDesktop(
        approval.requestId,
        "notification_unavailable",
      );
    }
    return sentCount;
  }

  async requestDesktop(
    requestId: string,
    source: "feishu_card" | "feishu_text" | "notification_unavailable",
  ): Promise<boolean> {
    const { store, feishu } = this.dependencies;
    const approval = await store.requestDesktopApproval(requestId);
    if (!approval) {
      return false;
    }
    const session = store.getSession(approval.sessionId);
    if (!session) {
      return true;
    }
    this.logEvent("desktop_requested", session, approval, {
      requestSource: source,
      elapsedSinceRequestMs: elapsedMs(approval.createdAt),
    });
    const card = buildDesktopApprovalCard(session, approval);
    await Promise.allSettled(
      approval.messageIds.map((messageId) => feishu.patchCard(messageId, card)),
    );
    return true;
  }

  async complete(
    requestId: string,
    resolution: ApprovalResolution,
    options: ApprovalCompletionOptions,
  ): Promise<boolean> {
    const activeCompletion = this.completions.get(requestId);
    if (activeCompletion) {
      await activeCompletion.catch(() => false);
      return false;
    }
    const completion = this.completeWithClaim(requestId, resolution, options);
    this.completions.set(requestId, completion);
    try {
      return await completion;
    } finally {
      if (this.completions.get(requestId) === completion) {
        this.completions.delete(requestId);
      }
    }
  }

  async awaitActiveCompletions(): Promise<void> {
    await Promise.allSettled([...this.completions.values()]);
  }

  async resolveForSession(sessionId: string): Promise<void> {
    const requestIds = this.dependencies.store
      .listApprovals()
      .filter(
        (approval) =>
          approval.sessionId === sessionId && approval.status === "pending",
      )
      .map((approval) => approval.requestId);
    for (const requestId of requestIds) {
      await this.complete(requestId, "local", { source: "session_closed" });
    }
  }

  async resolveAllForShutdown(): Promise<void> {
    const requestIds = this.dependencies.store
      .listApprovals()
      .filter((approval) => approval.status === "pending")
      .map((approval) => approval.requestId);
    for (const requestId of requestIds) {
      await this.complete(requestId, "local", { source: "shutdown" });
    }
  }

  hasPendingApprovals(): boolean {
    return this.dependencies.store
      .listApprovals()
      .some((approval) => approval.status === "pending");
  }

  listViews(): Array<Record<string, unknown>> {
    const { store } = this.dependencies;
    const recentCutoff = Date.now() - 10 * 60 * 1000;
    return store
      .listApprovals()
      .filter(
        (approval) =>
          approval.status === "pending" ||
          (approval.resolvedAt !== undefined &&
            Date.parse(approval.resolvedAt) >= recentCutoff),
      )
      .map((approval) => {
        const session = store.getSession(approval.sessionId);
        return {
          requestId: approval.requestId,
          sessionId: approval.sessionId,
          sessionLabel: session
            ? sessionLabel(session)
            : `#${shortSessionId(approval.sessionId)}`,
          projectName: session?.projectName ?? projectNameFromCwd(approval.cwd),
          cwd: approval.cwd,
          toolName: approval.toolName,
          toolPreview: approval.toolPreview,
          createdAt: approval.createdAt,
          expiresAt: approval.expiresAt,
          status: approval.status,
          requiresManualApproval: approval.requiresManualApproval !== false,
          desktopApprovalRequested:
            approval.desktopApprovalRequested ??
              (approval.requiresManualApproval !== false &&
                approval.messageIds.length === 0),
          resolution: approval.resolution ?? "",
          resolvedAt: approval.resolvedAt ?? "",
        };
      });
  }

  logEvent(
    event: string,
    session: SessionRecord,
    approval: ApprovalRecord,
    details: Record<string, unknown> = {},
    timestamp = Date.now(),
  ): void {
    const line = `[approval] ${JSON.stringify({
      event,
      at: new Date(timestamp).toISOString(),
      requestId: approval.requestId,
      runtime: session.runtime ?? "codex",
      sessionId: approval.sessionId,
      sessionShortId: session.shortId,
      tool: truncate(approval.toolName.replace(/\s+/gu, " "), 120),
      ...details,
    })}`;
    console.log(line);
    const approvalLogPath = this.dependencies.config.approvalLogPath;
    if (!approvalLogPath) {
      return;
    }
    this.logWrite = this.logWrite
      .then(() => this.appendLogLine(approvalLogPath, `${line}\n`))
      .catch((error) => {
        console.error("[approval] Failed to persist approval audit log:", error);
      });
  }

  async dispose(): Promise<void> {
    for (const waiter of this.waiters.values()) {
      clearTimeout(waiter.timer);
      waiter.resolve("local");
    }
    this.waiters.clear();
    this.notificationTimings.clear();
    for (const timer of this.standaloneTimeouts.values()) {
      clearTimeout(timer);
    }
    this.standaloneTimeouts.clear();
    await this.logWrite;
  }

  private async completeWithClaim(
    requestId: string,
    resolution: ApprovalResolution,
    options: ApprovalCompletionOptions,
  ): Promise<boolean> {
    const pending = await this.dependencies.store.claimApproval(requestId);
    if (!pending) {
      return false;
    }
    try {
      return await this.completeClaimed(pending, resolution, options);
    } finally {
      await this.dependencies.store.releaseApprovalClaim(requestId);
    }
  }

  private async completeClaimed(
    pending: ApprovalRecord,
    resolution: ApprovalResolution,
    options: ApprovalCompletionOptions,
  ): Promise<boolean> {
    const { store, feishu, opencode, onOpenCodePermissionForwarded } =
      this.dependencies;
    const requestId = pending.requestId;
    const sessionBeforeResolution = store.getSession(pending.sessionId);
    if (sessionBeforeResolution) {
      this.logEvent("decision_received", sessionBeforeResolution, pending, {
        resolution,
        decisionSource: options.source,
        elapsedSinceRequestMs: elapsedMs(pending.createdAt),
      });
    }
    let forwardedToOpenCode = false;
    if (
      options.forwardOpenCode !== false &&
      pending.opencodePermissionId &&
      (resolution === "allow" || resolution === "deny")
    ) {
      try {
        if (!opencode) {
          if (sessionBeforeResolution) {
            this.logEvent(
              "decision_forward_failed",
              sessionBeforeResolution,
              pending,
              {
                resolution,
                decisionSource: options.source,
                target: "opencode",
                error: "OpenCode manager unavailable",
                elapsedSinceRequestMs: elapsedMs(pending.createdAt),
              },
            );
          }
          return false;
        }
        await opencode.replyPermission(
          pending.sessionId,
          pending.opencodePermissionId,
          resolution === "allow" ? "once" : "reject",
        );
        forwardedToOpenCode = true;
      } catch (error) {
        if (sessionBeforeResolution) {
          this.logEvent(
            "decision_forward_failed",
            sessionBeforeResolution,
            pending,
            {
              resolution,
              decisionSource: options.source,
              target: "opencode",
              error: errorMessage(error),
              elapsedSinceRequestMs: elapsedMs(pending.createdAt),
            },
          );
        }
        console.error("[approval] Failed to forward decision to opencode:", error);
        return false;
      }
    }
    const approval = await store.resolveClaimedApproval(requestId, resolution);
    if (pending.opencodePermissionId && forwardedToOpenCode) {
      onOpenCodePermissionForwarded?.(
        pending.sessionId,
        pending.opencodePermissionId,
      );
    }
    if (!approval) {
      return false;
    }

    const waiter = this.waiters.get(requestId);
    if (waiter) {
      clearTimeout(waiter.timer);
      this.waiters.delete(requestId);
    }
    this.clearStandaloneTimeout(requestId);

    const session = await store.upsertSession({
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
      approval.messageIds.map((messageId) => feishu.patchCard(messageId, card)),
    );
    const resolvedAt = Date.parse(approval.resolvedAt ?? "");
    const completedAt = Number.isFinite(resolvedAt) ? resolvedAt : Date.now();
    const notificationTiming = this.notificationTimings.get(requestId);
    const notificationSentBeforeResolution = notificationTiming
      ? notificationTiming.firstSentAt <= completedAt
      : null;
    this.logEvent(
      "resolved",
      session,
      approval,
      {
        resolution,
        decisionSource: options.source,
        elapsedSinceRequestMs: elapsedMs(approval.createdAt, completedAt),
        notificationFirstSentAt: notificationTiming
          ? new Date(notificationTiming.firstSentAt).toISOString()
          : null,
        notificationLastSentAt: notificationTiming
          ? new Date(notificationTiming.lastSentAt).toISOString()
          : null,
        notificationSentBeforeResolution,
        elapsedSinceNotificationMs:
          notificationTiming && notificationSentBeforeResolution
            ? completedAt - notificationTiming.firstSentAt
            : null,
        notificationCount: notificationTiming?.count ?? approval.messageIds.length,
        forwardedToOpenCode,
      },
      completedAt,
    );
    this.notificationTimings.delete(requestId);
    return true;
  }

  private clearStandaloneTimeout(requestId: string): void {
    const timer = this.standaloneTimeouts.get(requestId);
    if (timer) {
      clearTimeout(timer);
      this.standaloneTimeouts.delete(requestId);
    }
  }

  private async appendLogLine(
    approvalLogPath: string,
    line: string,
  ): Promise<void> {
    const maxBytes = Math.max(
      1,
      this.dependencies.config.approvalLogMaxBytes ?? 5 * 1024 * 1024,
    );
    const maxBackups = Math.max(
      0,
      this.dependencies.config.approvalLogMaxBackups ?? 5,
    );
    let currentBytes = 0;
    try {
      currentBytes = (await stat(approvalLogPath)).size;
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") {
        throw error;
      }
    }
    if (currentBytes > 0 && currentBytes + Buffer.byteLength(line) > maxBytes) {
      await rotateLogFiles(approvalLogPath, maxBackups);
    }
    await appendFile(approvalLogPath, line, "utf8");
  }
}

export function actionToResolution(
  action: unknown,
): ApprovalResolution | undefined {
  switch (action) {
    case "approval_allow":
      return "allow";
    case "approval_deny":
      return "deny";
    default:
      return undefined;
  }
}

export function approvalActionFromText(
  text: string,
): ApprovalResolution | "desktop" | undefined {
  const normalized = text.replace(/[\s，。！!]/g, "").toLowerCase();
  if (["批准", "允许", "同意", "approve", "allow"].includes(normalized)) {
    return "allow";
  }
  if (["拒绝", "不允许", "deny", "reject"].includes(normalized)) {
    return "deny";
  }
  if (["本机确认", "本机审批", "电脑确认", "电脑审批", "pc审批"].includes(normalized)) {
    return "desktop";
  }
  return undefined;
}

export function approvalText(
  resolution: ApprovalResolution,
  session?: { runtime?: SessionRecord["runtime"] },
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

function elapsedMs(startAt: string | undefined, endAt = Date.now()): number | null {
  const start = startAt ? Date.parse(startAt) : Number.NaN;
  return Number.isFinite(start) ? Math.max(0, endAt - start) : null;
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

async function rotateLogFiles(logPath: string, maxBackups: number): Promise<void> {
  if (maxBackups === 0) {
    await rm(logPath, { force: true });
    return;
  }
  for (let index = maxBackups; index >= 1; index -= 1) {
    const source = index === 1 ? logPath : `${logPath}.${index - 1}`;
    const destination = `${logPath}.${index}`;
    await rm(destination, { force: true });
    try {
      await rename(source, destination);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") {
        throw error;
      }
    }
  }
}
