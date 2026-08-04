import { buildActivityCard, type ActivityCardEvent } from "./cards.js";
import type {
  ActivityHookPayload,
  MessageRouteKind,
  SessionRecord,
} from "./domain.js";
import { runtimeDisplayName } from "./domain.js";
import { FeishuGateway } from "./feishu.js";
import type { NotificationRecipient } from "./session-group-coordinator.js";
import { BridgeStore } from "./store.js";

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

interface ActivityCoordinatorDependencies {
  store: BridgeStore;
  feishu: FeishuGateway;
  recipients: (session: SessionRecord) => Promise<NotificationRecipient[]>;
  addRoute: (
    messageId: string,
    sessionId: string,
    chatId: string,
    kind: MessageRouteKind,
  ) => Promise<void>;
  watchSession: (session: SessionRecord) => Promise<void>;
}

export class ActivityCoordinator {
  private readonly states = new Map<string, ActivityState>();

  constructor(
    private readonly dependencies: ActivityCoordinatorDependencies,
  ) {}

  dispose(): void {
    for (const activity of this.states.values()) {
      if (activity.timer) {
        clearTimeout(activity.timer);
      }
    }
    this.states.clear();
  }

  rekey(previousSessionId: string, sessionId: string): void {
    const activity = this.states.get(previousSessionId);
    if (!activity) {
      return;
    }
    if (activity.timer) {
      clearTimeout(activity.timer);
      activity.timer = undefined;
    }
    this.states.delete(previousSessionId);
    activity.sessionId = sessionId;
    this.states.set(sessionId, activity);
    this.scheduleFlush(sessionId);
  }

  async record(payload: ActivityHookPayload): Promise<void> {
    const current = this.dependencies.store.getSession(payload.session_id);
    const isRemoteQuestion = payload.hook_event_name === "PreToolUse" &&
      payload.tool_name === "request_user_input";
    const session = !current ||
        (!isRemoteQuestion &&
          (current.status !== "running" ||
            (payload.turn_id && current.lastTurnId !== payload.turn_id)))
      ? await this.dependencies.store.upsertSession({
          sessionId: payload.session_id,
          cwd: payload.cwd,
          model: payload.model,
          turnId: payload.turn_id,
          status: "running",
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
        })
      : current;
    await this.dependencies.watchSession(session);
    let state = this.states.get(payload.session_id);
    if (state && payload.turn_id && state.turnId && state.turnId !== payload.turn_id) {
      await this.finish(payload.session_id, "上一轮已结束");
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
      this.states.set(payload.session_id, state);
    }
    state.turnId ??= payload.turn_id;
    state.events.push(activityEventFromPayload(payload));
    state.events = state.events.slice(-6);
    state.revision += 1;
    this.scheduleFlush(session.sessionId);
  }

  async finish(sessionId: string, label: string): Promise<void> {
    const state = this.states.get(sessionId);
    if (!state) {
      return;
    }
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
    await this.flush(sessionId, true);
    this.states.delete(sessionId);
  }

  private scheduleFlush(sessionId: string): void {
    const state = this.states.get(sessionId);
    if (!state || state.timer || state.completed) {
      return;
    }
    const delay = Math.max(0, 2_000 - (Date.now() - state.lastSentAt));
    state.timer = setTimeout(() => {
      state.timer = undefined;
      void this.flush(sessionId).catch((error) => {
        console.error("[activity] Could not update Feishu progress card:", error);
      });
    }, delay);
  }

  private async flush(sessionId: string, force = false): Promise<void> {
    const state = this.states.get(sessionId);
    if (!state) {
      return;
    }
    if (state.flushing) {
      await state.flushing;
      if (force && state.sentRevision < state.revision) {
        await this.flush(sessionId, true);
      }
      return;
    }
    if (!force && state.sentRevision >= state.revision) {
      return;
    }
    const capturedRevision = state.revision;
    const operation = (async () => {
      const session = this.dependencies.store.getSession(sessionId);
      if (!session) {
        return;
      }
      const card = buildActivityCard(
        session,
        state.events,
        state.startedAt,
        state.completed,
      );
      for (const recipient of await this.dependencies.recipients(session)) {
        try {
          const existingMessageId = state.messageIds.get(recipient.chatId);
          if (existingMessageId) {
            await this.dependencies.feishu.patchCard(existingMessageId, card);
          } else {
            const messageId = await this.dependencies.feishu.sendCard(
              recipient.chatId,
              card,
            );
            state.messageIds.set(recipient.chatId, messageId);
            await this.dependencies.addRoute(
              messageId,
              sessionId,
              recipient.chatId,
              "activity",
            );
          }
        } catch (error) {
          console.error(
            "[activity] Failed to send or patch a progress card:",
            error,
          );
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
      this.scheduleFlush(sessionId);
    }
  }
}

function activityEventFromPayload(
  payload: ActivityHookPayload,
): ActivityCardEvent {
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
    case "PostToolUseFailure":
      return {
        at,
        label: `${humanizeToolName(payload.tool_name)} 执行失败`,
        detail: payload.tool_response_preview,
      };
    case "PreCompact":
      return { at, label: "正在压缩上下文" };
    case "PostCompact":
      return { at, label: "上下文压缩完成" };
    case "UserPromptSubmit":
      return {
        at,
        label: `已提交新任务，${runtimeDisplayName(payload.runtime)} 开始处理`,
      };
  }
}

function humanizeToolName(toolName: string | undefined): string {
  if (!toolName) {
    return "工具";
  }
  const known: Record<string, string> = {
    shell_command: "命令行",
    apply_patch: "文件修改",
    view_image: "图片查看",
    request_user_input: "用户提问",
  };
  return known[toolName] ?? toolName.replace(/^mcp__/, "MCP · ");
}
