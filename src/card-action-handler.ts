import {
  actionToResolution,
  ApprovalCoordinator,
  approvalText,
} from "./approval-coordinator.js";
import {
  buildRuntimeLaunchCancelledCard,
  buildRuntimeLaunchSubmittedCard,
  buildRuntimeProjectFormCard,
} from "./cards.js";
import {
  isRuntimeName,
  runtimeDisplayName,
  truncate,
} from "./domain.js";
import { projectDirectoryNameValidationError } from "./message-command-parser.js";
import { RuntimeLaunchCoordinator } from "./runtime-launch-coordinator.js";
import { RuntimeRetryCoordinator } from "./runtime-retry-coordinator.js";
import { BridgeStore } from "./store.js";
import { UserInputCoordinator } from "./user-input-coordinator.js";

type FeishuEvent = Record<string, any>;

export interface CardActionResult {
  toast: {
    type: "success" | "warning" | "error" | "info";
    content: string;
  };
  card?: Record<string, unknown>;
}

export class CardActionHandler {
  private readonly runtimeNewFlows = new Map<
    string,
    "submitting" | "submitted" | "cancelled"
  >();

  constructor(
    private readonly store: BridgeStore,
    private readonly approvals: ApprovalCoordinator,
    private readonly inputs: UserInputCoordinator,
    private readonly runtimeRetries: RuntimeRetryCoordinator,
    private readonly runtimeLaunches: RuntimeLaunchCoordinator,
  ) {}

  async handle(data: FeishuEvent): Promise<CardActionResult> {
    const actionValue = normalizeActionValue(data.action?.value);
    const operatorOpenId = data.operator?.open_id;
    console.log(
      `[card] handling action=${typeof actionValue?.action === "string" ? actionValue.action : "unknown"} operator=${typeof operatorOpenId === "string" ? "present" : "missing"}`,
    );

    if (!operatorOpenId || !this.store.isBound(operatorOpenId)) {
      return { toast: { type: "warning", content: "只有已绑定的管理员可以操作。" } };
    }
    if (!actionValue) {
      return { toast: { type: "error", content: "卡片操作参数不完整。" } };
    }
    const action = actionValue.action;
    const requestId = actionValue.requestId;

    if (
      action === "runtime_new_select" ||
      action === "runtime_new_submit" ||
      action === "runtime_new_cancel"
    ) {
      return await this.handleRuntimeNewAction(action, actionValue, data);
    }

    if (action === "retry_stop") {
      const sessionId = actionValue?.sessionId;
      const retryCycleId = actionValue?.retryCycleId;
      if (typeof sessionId !== "string" || typeof retryCycleId !== "string") {
        return { toast: { type: "error", content: "自动重试参数不完整。" } };
      }
      const result = await this.runtimeRetries.stop(sessionId, retryCycleId);
      if (result.kind === "stale") {
        return {
          toast: {
            type: "warning",
            content: "这轮自动重试已经结束，或已被新的任务替代。",
          },
        };
      }
      return {
        toast: {
          type: result.kind === "already_stopped" ? "info" : "success",
          content: result.retryAlreadyStarted
            ? "本次重试已经发送，已停止后续自动重试。"
            : result.kind === "already_stopped"
              ? "自动重试已经停止。"
              : "已停止自动重试。",
        },
        card: result.card,
      };
    }
    if (typeof requestId !== "string") {
      return { toast: { type: "error", content: "审批参数不完整。" } };
    }

    if (
      action === "input_answer" ||
      action === "input_toggle" ||
      action === "input_submit" ||
      action === "input_local"
    ) {
      return await this.handleInputAction(
        action,
        actionValue,
        requestId,
        operatorOpenId,
      );
    }

    if (action === "approval_desktop") {
      const approval = this.store.getApproval(requestId);
      if (
        !approval ||
        approval.status !== "pending" ||
        (typeof actionValue?.sessionId === "string" &&
          approval.sessionId !== actionValue.sessionId)
      ) {
        return { toast: { type: "warning", content: "这条审批已经处理或失效。" } };
      }
      const requested = await this.approvals.requestDesktop(
        requestId,
        "feishu_card",
      );
      return {
        toast: {
          type: requested ? "success" : "warning",
          content: requested
            ? "已转回 PC 审批，请在电脑端审批窗口处理。"
            : "这条审批已经处理或失效。",
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

    const completed = await this.approvals.complete(requestId, resolution, {
      source: "feishu_card",
    });
    return {
      toast: {
        type: completed ? "success" : "warning",
        content: completed
          ? approvalText(resolution, this.store.getSession(approval.sessionId))
          : "这条审批已经处理或失效。",
      },
    };
  }

  private async handleRuntimeNewAction(
    action: "runtime_new_select" | "runtime_new_submit" | "runtime_new_cancel",
    actionValue: Record<string, unknown>,
    data: FeishuEvent,
  ): Promise<CardActionResult> {
    const flowId = normalizeShortString(actionValue.flowId, 128);
    const runtime = actionValue.runtime;
    const context = normalizeRuntimeNewContext(data, actionValue);
    if (!flowId || !isRuntimeName(runtime) || !context) {
      return { toast: { type: "error", content: "新建会话卡片参数不完整。" } };
    }

    const state = this.runtimeNewFlows.get(flowId);
    if (action === "runtime_new_select") {
      if (state) {
        return { toast: { type: "warning", content: "这次新建操作已经处理或失效。" } };
      }
      console.log(`[runtime-new] selected runtime=${runtime} flow=${flowId.slice(0, 12)}`);
      return {
        toast: {
          type: "info",
          content: `已选择 ${runtimeDisplayName(runtime)}，请填写项目名。`,
        },
        card: buildRuntimeProjectFormCard(
          runtime,
          this.store.getSettings().workspaceRoot || undefined,
          { flowId, sourceMessageId: context.sourceMessageId, chatId: context.chatId },
        ),
      };
    }

    if (action === "runtime_new_cancel") {
      if (state === "submitting" || state === "submitted") {
        return { toast: { type: "warning", content: "启动请求已经提交，不能再取消。" } };
      }
      console.log(`[runtime-new] cancelled runtime=${runtime} flow=${flowId.slice(0, 12)}`);
      this.rememberRuntimeNewFlow(flowId, "cancelled");
      return {
        toast: {
          type: state === "cancelled" ? "info" : "success",
          content: "已取消新建会话。",
        },
        card: buildRuntimeLaunchCancelledCard(runtime),
      };
    }

    if (state) {
      return {
        toast: {
          type: "warning",
          content: state === "cancelled"
            ? "这次新建操作已经取消。"
            : "启动请求已经提交，请勿重复点击。",
        },
      };
    }

    const projectName = normalizeProjectName(data.action?.form_value?.project_name)
      ?? normalizeProjectName(data.action?.formValue?.project_name)
      ?? normalizeProjectName(data.form_value?.project_name);
    if (!projectName) {
      return { toast: { type: "error", content: "请输入项目名。" } };
    }
    const validationError = projectDirectoryNameValidationError(projectName);
    if (validationError) {
      return {
        toast: { type: "error", content: `项目名不正确：${validationError}` },
      };
    }
    const workspaceRoot = this.store.getSettings().workspaceRoot;
    if (!workspaceRoot) {
      return {
        toast: {
          type: "error",
          content: "尚未设置默认工作区，请先在电脑端“设置”中选择。",
        },
      };
    }

    this.rememberRuntimeNewFlow(flowId, "submitting");
    console.log(`[runtime-new] submitting runtime=${runtime} project=${projectName} flow=${flowId.slice(0, 12)}`);
    let queued: boolean;
    try {
      queued = await this.runtimeLaunches.handleNewCommand(
        { runtime, projectName },
        context.sourceMessageId,
        context.chatId,
      );
    } catch (error) {
      this.runtimeNewFlows.delete(flowId);
      throw error;
    }
    if (!queued) {
      this.runtimeNewFlows.delete(flowId);
      return {
        toast: {
          type: "error",
          content: "新建请求未提交，请查看机器人回复后修改并重试。",
        },
      };
    }

    this.runtimeNewFlows.set(flowId, "submitted");
    return {
      toast: {
        type: "success",
        content: `已提交 ${runtimeDisplayName(runtime)} 启动请求。`,
      },
      card: buildRuntimeLaunchSubmittedCard(runtime, projectName, workspaceRoot),
    };
  }

  private rememberRuntimeNewFlow(
    flowId: string,
    state: "submitting" | "submitted" | "cancelled",
  ): void {
    if (this.runtimeNewFlows.size >= 500) {
      const oldest = this.runtimeNewFlows.keys().next().value;
      if (typeof oldest === "string") {
        this.runtimeNewFlows.delete(oldest);
      }
    }
    this.runtimeNewFlows.set(flowId, state);
  }

  private async handleInputAction(
    action: "input_answer" | "input_toggle" | "input_submit" | "input_local",
    actionValue: Record<string, unknown>,
    requestId: string,
    operatorOpenId: string,
  ): Promise<CardActionResult> {
    const waiter = this.inputs.get(requestId);
    if (
      !waiter ||
      (typeof actionValue.sessionId === "string" &&
        waiter.sessionId !== actionValue.sessionId)
    ) {
      return { toast: { type: "warning", content: "这组问题已经处理或失效。" } };
    }
    if (action === "input_local") {
      const completed = await this.inputs.complete(requestId, { kind: "local" });
      return {
        toast: {
          type: completed ? "success" : "warning",
          content: completed ? "已转回电脑端回答。" : "这组问题已经处理或失效。",
        },
      };
    }
    const questionId = actionValue.questionId;
    if (typeof questionId !== "string") {
      return { toast: { type: "error", content: "问题参数不完整。" } };
    }
    const question = waiter.questions.find((item) => item.id === questionId);
    if (!question) {
      return { toast: { type: "error", content: "这个问题已经失效。" } };
    }
    const suppliedSelectionKey = actionValue.selectionKey;
    const selectionKey = typeof suppliedSelectionKey === "string"
      ? suppliedSelectionKey
      : `operator:${operatorOpenId}`;
    if (
      typeof suppliedSelectionKey === "string" &&
      !waiter.messageCards.some(
        (item) =>
          item.questionId === questionId && item.chatId === suppliedSelectionKey,
      )
    ) {
      return { toast: { type: "warning", content: "这张问题卡已经失效。" } };
    }
    if (action === "input_toggle") {
      const answer = actionValue.answer;
      if (!question.multiple || typeof answer !== "string") {
        return { toast: { type: "error", content: "多选答案参数不完整。" } };
      }
      if (!question.options.some((option) => option.label === answer)) {
        return { toast: { type: "error", content: "这个答案不属于当前问题。" } };
      }
      const result = await this.inputs.toggleAnswer(
        requestId,
        questionId,
        answer,
        selectionKey,
      );
      return {
        toast: {
          type: result === "stale" ? "warning" : "success",
          content: result === "stale"
            ? "这道问题已经处理或失效。"
            : result === "selected"
              ? `已选择“${truncate(answer, 80)}”，可继续选择或提交。`
              : `已取消“${truncate(answer, 80)}”的选择。`,
        },
      };
    }
    if (action === "input_submit") {
      if (!question.multiple) {
        return { toast: { type: "error", content: "这不是多选问题。" } };
      }
      const result = await this.inputs.submitQuestion(
        requestId,
        questionId,
        selectionKey,
      );
      const runtime = runtimeDisplayName(
        this.store.getSession(waiter.sessionId)?.runtime,
      );
      return {
        toast: {
          type: result === "failed" || result === "stale"
            ? "warning"
            : result === "empty"
              ? "error"
              : "success",
          content: result === "submitted"
            ? `已把答案交给 ${runtime}。`
            : result === "recorded"
              ? "已记录这道问题，请继续处理其他问题。"
              : result === "empty"
                ? "请至少选择一个选项。"
                : result === "failed"
                  ? "暂时无法提交，请稍后重试。"
                  : "这道问题已经处理或失效。",
        },
      };
    }
    const answer = actionValue.answer;
    if (
      typeof answer !== "string" ||
      !question.options.some((option) => option.label === answer)
    ) {
      return { toast: { type: "error", content: "这个答案不属于当前问题。" } };
    }
    const result = await this.inputs.recordAnswer(requestId, questionId, [answer]);
    const runtime = runtimeDisplayName(
      this.store.getSession(waiter.sessionId)?.runtime,
    );
    return {
      toast: {
        type: result === "submitted" || result === "recorded"
          ? "success"
          : "warning",
        content: result === "submitted"
          ? `已把答案交给 ${runtime}。`
          : result === "recorded"
            ? "已记录这道问题，请继续处理其他问题。"
            : result === "failed"
              ? "暂时无法提交，请稍后重试。"
              : "这道问题已经处理或失效。",
      },
    };
  }
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

function normalizeRuntimeNewContext(
  data: FeishuEvent,
  actionValue: Record<string, unknown>,
): { sourceMessageId: string; chatId: string } | undefined {
  const callbackChatId = normalizeShortString(
    data.context?.open_chat_id ?? data.open_chat_id,
    256,
  );
  const valueChatId = normalizeShortString(actionValue.chatId, 256);
  if (callbackChatId && valueChatId && callbackChatId !== valueChatId) {
    return undefined;
  }
  const chatId = callbackChatId ?? valueChatId;
  const sourceMessageId = normalizeShortString(actionValue.sourceMessageId, 256)
    ?? normalizeShortString(
      data.context?.open_message_id ?? data.open_message_id,
      256,
    );
  return sourceMessageId && chatId ? { sourceMessageId, chatId } : undefined;
}

function normalizeProjectName(value: unknown): string | undefined {
  if (typeof value !== "string") {
    return undefined;
  }
  const normalized = value.trim().normalize("NFC");
  return normalized || undefined;
}

function normalizeShortString(value: unknown, maxLength: number): string | undefined {
  if (typeof value !== "string") {
    return undefined;
  }
  const normalized = value.trim();
  return normalized && normalized.length <= maxLength ? normalized : undefined;
}
