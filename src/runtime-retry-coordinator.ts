import { randomUUID } from "node:crypto";

import { buildErrorCards } from "./cards.js";
import type { SessionRecord } from "./domain.js";
import { isRetryableRuntimeError, retryDelayMs } from "./runtime-errors.js";
import { BridgeStore } from "./store.js";
import type { TurnNotificationDelivery } from "./turn-notification-coordinator.js";

interface RuntimeRetryCoordinatorDependencies {
  store: BridgeStore;
  retryBaseDelayMs?: number;
  finishActivity: (sessionId: string, label: string) => Promise<void>;
  isRuntimeReady: (session: SessionRecord) => boolean;
  sendRetry: (session: SessionRecord, prompt: string) => Promise<void>;
  sendErrorNotification: (
    session: SessionRecord,
    turnId: string,
    errorMessage: string,
    cards: Record<string, unknown>[],
  ) => Promise<TurnNotificationDelivery>;
  patchCard: (
    messageId: string,
    card: Record<string, unknown>,
  ) => Promise<void>;
  releaseRetryLock: (sessionId: string) => void | Promise<void>;
}

export interface RuntimeRetryStrategy {
  isReady?: (session: SessionRecord) => boolean;
  sendRetry?: (session: SessionRecord, prompt: string) => Promise<void>;
  releaseRetryLock?: (sessionId: string) => void | Promise<void>;
  retryPrompt?: string;
}

type RetryPhase = "preparing" | "scheduled" | "running" | "stopped";

interface RuntimeRetryCycle {
  id: string;
  sessionId: string;
  turnId: string;
  errorMessage: string;
  attempt: number;
  maxAttempts: number;
  delayMs: number;
  phase: RetryPhase;
  stopped: boolean;
  messageIds: string[];
  isReady: (session: SessionRecord) => boolean;
  sendRetry: (session: SessionRecord, prompt: string) => Promise<void>;
  releaseRetryLock: (sessionId: string) => void | Promise<void>;
  retryPrompt: string;
  timer?: NodeJS.Timeout;
}

export type StopRuntimeRetryResult =
  | {
      kind: "stopped" | "already_stopped";
      retryAlreadyStarted: boolean;
      card: Record<string, unknown>;
    }
  | { kind: "stale" };

export class RuntimeRetryCoordinator {
  private readonly retryCounts = new Map<string, number>();
  private readonly cycles = new Map<string, RuntimeRetryCycle>();

  constructor(
    private readonly dependencies: RuntimeRetryCoordinatorDependencies,
  ) {}

  hasActiveRetry(sessionId: string): boolean {
    const cycle = this.cycles.get(sessionId);
    return Boolean(cycle && !cycle.stopped && cycle.phase !== "stopped");
  }

  async beginManualTurn(sessionId: string): Promise<void> {
    const cycle = this.cycles.get(sessionId);
    const shouldRelease = Boolean(
      cycle &&
        !cycle.stopped &&
        cycle.phase !== "running",
    );
    this.reset(sessionId);
    if (shouldRelease) {
      await cycle?.releaseRetryLock(sessionId);
    }
  }

  reset(sessionId: string): void {
    const cycle = this.cycles.get(sessionId);
    if (cycle?.timer) {
      clearTimeout(cycle.timer);
    }
    this.cycles.delete(sessionId);
    this.retryCounts.delete(sessionId);
  }

  dispose(): void {
    for (const cycle of this.cycles.values()) {
      if (cycle.timer) {
        clearTimeout(cycle.timer);
      }
    }
    this.cycles.clear();
    this.retryCounts.clear();
  }

  async notifyTurnError(
    session: SessionRecord,
    turnId: string,
    errorMessage: string,
    errorCode?: string,
    strategy: RuntimeRetryStrategy = {},
  ): Promise<boolean> {
    const { store } = this.dependencies;
    if (turnNotificationWasSent(store.getSession(session.sessionId), turnId)) {
      return false;
    }
    await this.dependencies.finishActivity(session.sessionId, "本轮发生错误");
    const retryCount = this.retryCounts.get(session.sessionId) ?? 0;
    const retrySettings = store.getSettings();
    const retryDelay = retryDelayMs(
      retrySettings,
      this.dependencies.retryBaseDelayMs,
    );
    const existingCycle = this.cycles.get(session.sessionId);
    const canRetry =
      retrySettings.autoRetryErrors &&
      !existingCycle?.stopped &&
      retryCount < retrySettings.retryMaxAttempts &&
      isRetryableRuntimeError(errorMessage, errorCode) &&
      (strategy.isReady ?? this.dependencies.isRuntimeReady)(session);
    const retryAttempt = retryCount + 1;
    const cycle = canRetry
      ? this.prepareCycle(
          existingCycle,
          session.sessionId,
          turnId,
          errorMessage,
          retryAttempt,
          retrySettings.retryMaxAttempts,
          retryDelay,
          strategy,
        )
      : undefined;
    const detail = cycle
      ? retryDetail(cycle, "scheduled")
      : errorMessage;
    const failedSession = await store.upsertSession({
      sessionId: session.sessionId,
      cwd: session.cwd,
      model: session.model,
      turnId,
      status: "error",
      error: errorMessage,
      runtime: session.runtime,
    });
    const delivery = await this.dependencies.sendErrorNotification(
      failedSession,
      turnId,
      errorMessage,
      buildErrorCards(
        failedSession,
        detail,
        cycle
          ? { cycleId: cycle.id, state: "scheduled" }
          : undefined,
      ),
    );
    if (!cycle) {
      return false;
    }
    cycle.messageIds = delivery.messageIds;
    if (cycle.stopped || this.cycles.get(session.sessionId) !== cycle) {
      await this.patchCycleCards(failedSession, cycle, "stopped");
      return false;
    }

    this.retryCounts.set(session.sessionId, retryAttempt);
    const retryTimer = setTimeout(() => {
      void this.runScheduledRetry(cycle);
    }, retryDelay);
    cycle.timer = retryTimer;
    cycle.phase = "scheduled";
    retryTimer.unref?.();
    return true;
  }

  async stop(
    sessionId: string,
    retryCycleId: string,
  ): Promise<StopRuntimeRetryResult> {
    const cycle = this.cycles.get(sessionId);
    if (!cycle || cycle.id !== retryCycleId) {
      return { kind: "stale" };
    }
    const retryAlreadyStarted = cycle.phase === "running";
    if (cycle.stopped) {
      return {
        kind: "already_stopped",
        retryAlreadyStarted,
        card: this.stoppedCard(cycle),
      };
    }
    cycle.stopped = true;
    cycle.phase = "stopped";
    if (cycle.timer) {
      clearTimeout(cycle.timer);
      cycle.timer = undefined;
    }
    const session = this.dependencies.store.getSession(sessionId);
    if (session) {
      await this.patchCycleCards(session, cycle, "stopped");
    }
    if (!retryAlreadyStarted) {
      await cycle.releaseRetryLock(sessionId);
    }
    return {
      kind: "stopped",
      retryAlreadyStarted,
      card: this.stoppedCard(cycle),
    };
  }

  private prepareCycle(
    existing: RuntimeRetryCycle | undefined,
    sessionId: string,
    turnId: string,
    errorMessage: string,
    attempt: number,
    maxAttempts: number,
    delayMs: number,
    strategy: RuntimeRetryStrategy,
  ): RuntimeRetryCycle {
    if (existing?.timer) {
      clearTimeout(existing.timer);
    }
    const cycle: RuntimeRetryCycle = {
      id: existing?.id ?? randomUUID(),
      sessionId,
      turnId,
      errorMessage,
      attempt,
      maxAttempts,
      delayMs,
      phase: "preparing",
      stopped: false,
      messageIds: [],
      isReady: strategy.isReady ?? this.dependencies.isRuntimeReady,
      sendRetry: strategy.sendRetry ?? this.dependencies.sendRetry,
      releaseRetryLock:
        strategy.releaseRetryLock ?? this.dependencies.releaseRetryLock,
      retryPrompt: strategy.retryPrompt ??
        "刚才的请求因临时服务错误失败。请重试上一项任务，并继续从中断处执行。",
    };
    this.cycles.set(sessionId, cycle);
    return cycle;
  }

  private async runScheduledRetry(cycle: RuntimeRetryCycle): Promise<void> {
    if (
      this.cycles.get(cycle.sessionId) !== cycle ||
      cycle.stopped ||
      cycle.phase !== "scheduled"
    ) {
      return;
    }
    cycle.timer = undefined;
    const current = this.dependencies.store.getSession(cycle.sessionId);
    const settings = this.dependencies.store.getSettings();
    if (
      !settings.autoRetryErrors ||
      cycle.attempt > settings.retryMaxAttempts ||
      this.retryCounts.get(cycle.sessionId) !== cycle.attempt ||
      !current ||
      !cycle.isReady(current)
    ) {
      cycle.stopped = true;
      cycle.phase = "stopped";
      if (current) {
        await this.patchCycleCards(current, cycle, "stopped");
      }
      await cycle.releaseRetryLock(cycle.sessionId);
      return;
    }

    cycle.phase = "running";
    await this.patchCycleCards(current, cycle, "running");
    try {
      await cycle.sendRetry(current, cycle.retryPrompt);
    } catch (error) {
      cycle.stopped = true;
      cycle.phase = "stopped";
      console.error("[retry] Runtime retry failed:", error);
      await this.patchCycleCards(current, cycle, "stopped");
      await cycle.releaseRetryLock(cycle.sessionId);
    }
  }

  private stoppedCard(cycle: RuntimeRetryCycle): Record<string, unknown> {
    const session = this.dependencies.store.getSession(cycle.sessionId);
    if (!session) {
      return {};
    }
    return buildErrorCards(
      session,
      retryDetail(cycle, "stopped"),
      { cycleId: cycle.id, state: "stopped" },
    ).at(-1) ?? {};
  }

  private async patchCycleCards(
    session: SessionRecord,
    cycle: RuntimeRetryCycle,
    state: "running" | "stopped",
  ): Promise<void> {
    const cards = buildErrorCards(
      session,
      retryDetail(cycle, state),
      { cycleId: cycle.id, state },
    );
    if (cards.length === 0) {
      return;
    }
    await Promise.allSettled(
      cycle.messageIds.map((messageId, index) =>
        this.dependencies.patchCard(
          messageId,
          cards[index % cards.length]!,
        )
      ),
    );
  }
}

function retryDetail(
  cycle: RuntimeRetryCycle,
  state: "scheduled" | "running" | "stopped",
): string {
  switch (state) {
    case "scheduled":
      return `${cycle.errorMessage}\n\n助手将在 ${Math.ceil(cycle.delayMs / 1_000)} 秒后自动重试（第 ${cycle.attempt}/${cycle.maxAttempts} 次）。`;
    case "running":
      return `${cycle.errorMessage}\n\n助手已发起第 ${cycle.attempt}/${cycle.maxAttempts} 次自动重试。`;
    case "stopped":
      return `${cycle.errorMessage}\n\n已停止自动重试。`;
  }
}

function turnNotificationWasSent(
  session: SessionRecord | undefined,
  turnId: string,
): boolean {
  return session?.lastNotificationTurnId === turnId &&
    session.lastNotificationStatus !== "pending";
}
