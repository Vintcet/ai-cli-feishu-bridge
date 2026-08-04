import type {
  ApprovalRecord,
  ApprovalResolution,
  SessionRecord,
  UserInputQuestion,
} from "./domain.js";
import {
  looksLikeQuestion,
  redactSensitiveText,
  runtimeDisplayName,
  sessionLabel,
  truncate,
} from "./domain.js";
import {
  markdownToFeishuCardElements,
  splitTextForFeishu,
} from "./feishu-markdown.js";

type Card = Record<string, unknown>;

export interface ActivityCardEvent {
  at: string;
  label: string;
  detail?: string;
}

function approvalResultText(
  resolution: ApprovalResolution,
  session: SessionRecord,
): string {
  const runtime = runtimeDisplayName(session.runtime);
  switch (resolution) {
    case "allow":
      return `已批准，${runtime} 将继续执行。`;
    case "deny":
      return `已拒绝，${runtime} 会收到拒绝结果。`;
    case "local":
      return `已转回电脑端，请在原 ${runtime} 窗口确认。`;
    case "timeout":
      return "飞书审批已超时，已转回电脑端确认。";
  }
}

export function buildApprovalCard(
  session: SessionRecord,
  approval: ApprovalRecord,
): Card {
  const runtime = runtimeDisplayName(session.runtime);
  const detail = truncate(approval.toolPreview || "（没有可展示的参数）", 2600);
  return {
    config: { wide_screen_mode: true, update_multi: true },
    header: {
      template: approval.riskLevel === "high" ? "red" : "orange",
      title: {
        tag: "plain_text",
        content: approval.riskLevel === "high"
          ? `${runtime} 高风险操作需要确认`
          : `${runtime} 需要你的确认`,
      },
    },
    elements: [
      {
        tag: "div",
        text: {
          tag: "lark_md",
          content: `**会话：** ${sessionLabel(session)}\n**工具：** ${approval.toolName}\n**目录：** ${session.cwd}${
            approval.riskLevel === "high"
              ? `\n**风险：** 高（${approval.riskReason ?? "命中高风险规则"}）`
              : ""
          }`,
        },
      },
      { tag: "hr" },
      {
        tag: "div",
        text: {
          tag: "lark_md",
          content: `**请求内容**\n\`\`\`\n${detail}\n\`\`\``,
        },
      },
      {
        tag: "note",
        elements: [
          {
            tag: "plain_text",
            content: "审批默认只在飞书等待；需要电脑端窗口时，请点击“转回 PC 审批”。",
          },
        ],
      },
      {
        tag: "action",
        actions: [
          {
            tag: "button",
            type: "primary",
            text: { tag: "plain_text", content: "批准一次" },
            value: {
              action: "approval_allow",
              requestId: approval.requestId,
              sessionId: approval.sessionId,
            },
          },
          {
            tag: "button",
            type: "danger",
            text: { tag: "plain_text", content: "拒绝" },
            value: {
              action: "approval_deny",
              requestId: approval.requestId,
              sessionId: approval.sessionId,
            },
          },
          {
            tag: "button",
            type: "default",
            text: { tag: "plain_text", content: "转回 PC 审批" },
            value: {
              action: "approval_desktop",
              requestId: approval.requestId,
              sessionId: approval.sessionId,
            },
          },
        ],
      },
    ],
  };
}

export function buildDesktopApprovalCard(
  session: SessionRecord,
  approval: ApprovalRecord,
): Card {
  const runtime = runtimeDisplayName(session.runtime);
  return {
    config: { wide_screen_mode: true, update_multi: true },
    header: {
      template: "blue",
      title: { tag: "plain_text", content: `${runtime} 已转回 PC 审批` },
    },
    elements: [
      {
        tag: "div",
        text: {
          tag: "lark_md",
          content: `**会话：** ${sessionLabel(session)}\n**工具：** ${approval.toolName}\n\n已通知 AI CLI 飞书助手，请在电脑端审批窗口处理。`,
        },
      },
    ],
  };
}

export function buildResolvedApprovalCard(
  session: SessionRecord,
  approval: ApprovalRecord,
  resolution: ApprovalResolution,
): Card {
  const runtime = runtimeDisplayName(session.runtime);
  const template = resolution === "allow" ? "green" : resolution === "deny" ? "red" : "grey";
  return {
    config: { wide_screen_mode: true, update_multi: true },
    header: {
      template,
      title: { tag: "plain_text", content: `${runtime} 审批已处理` },
    },
    elements: [
      {
        tag: "div",
        text: {
          tag: "lark_md",
          content: `**会话：** ${sessionLabel(session)}\n**工具：** ${approval.toolName}\n\n${approvalResultText(resolution, session)}`,
        },
      },
    ],
  };
}

export interface UserInputCardState {
  selectedAnswers?: readonly string[];
  answered?: boolean;
  remainingQuestions?: number;
}

/**
 * Build one interactive card per question so a Feishu reply never has to
 * encode several answers into one positional string.
 */
export function buildUserInputCards(
  session: SessionRecord,
  requestId: string,
  questions: UserInputQuestion[],
  selectionKey?: string,
): Card[] {
  return questions.map((question, questionIndex) =>
    buildUserInputQuestionCard(
      session,
      requestId,
      question,
      questionIndex,
      questions.length,
      {},
      selectionKey,
    ));
}

export function buildUserInputQuestionCard(
  session: SessionRecord,
  requestId: string,
  question: UserInputQuestion,
  questionIndex: number,
  questionCount: number,
  state: UserInputCardState = {},
  selectionKey?: string,
): Card {
  const runtime = runtimeDisplayName(session.runtime);
  const selected = [...(state.selectedAnswers ?? [])];
  const answered = state.answered === true;
  const behaviorNotes = [
    question.multiple ? "可多选" : "单选",
    question.custom === false ? "仅限所列选项" : "可填写自定义答案",
  ].join(" · ");
  const options = question.options
    .map(
      (option, optionIndex) =>
        `${optionIndex + 1}. **${truncate(option.label, 80)}**${
          option.description ? ` — ${truncate(option.description, 180)}` : ""
        }${option.preview ? `\n   > 预览：${truncate(option.preview.replace(/\s+/gu, " "), 260)}` : ""}`,
    )
    .join("\n");
  const answerText = question.isSecret
    ? "已提供（已隐藏）"
    : selected.length > 0
      ? truncate(selected.join("、"), 500)
      : "尚未选择";
  const elements: Record<string, unknown>[] = [
    {
      tag: "div",
      text: {
        tag: "lark_md",
        content: `**会话：** ${sessionLabel(session)}\n**目录：** ${session.cwd}`,
      },
    },
    { tag: "hr" },
    {
      tag: "div",
      text: {
        tag: "lark_md",
        content: `**${questionIndex + 1}/${questionCount}. ${truncate(question.header, 80)}**\n${truncate(question.question, 800)}\n_${behaviorNotes}_${question.isSecret ? "\n\n_此答案不会在处理结果卡片中回显。_" : ""}${options ? `\n\n${options}` : ""}`,
      },
    },
  ];

  if (answered) {
    elements.push({
      tag: "note",
      elements: [{
        tag: "plain_text",
        content: state.remainingQuestions && state.remainingQuestions > 0
          ? `已记录：${answerText}；还剩 ${state.remainingQuestions} 个问题。`
          : `已记录：${answerText}。`,
      }],
    });
  } else {
    const actions: Record<string, unknown>[] = question.options.map((option, optionIndex) => {
      const isSelected = selected.includes(option.label);
      return {
        tag: "button",
        ...(question.multiple
          ? isSelected
            ? { type: "primary" }
            : {}
          : optionIndex === 0
            ? { type: "primary" }
            : {}),
        text: {
          tag: "plain_text",
          content: question.multiple && isSelected
            ? `✓ ${truncate(option.label, 36)}`
            : truncate(option.label, 40),
        },
        value: {
          action: question.multiple ? "input_toggle" : "input_answer",
          requestId,
          sessionId: session.sessionId,
          questionId: question.id,
          answer: option.label,
          ...(selectionKey ? { selectionKey } : {}),
        },
      };
    });
    if (question.multiple) {
      actions.push({
        tag: "button",
        type: "primary",
        text: { tag: "plain_text", content: "提交选择" },
        value: {
          action: "input_submit",
          requestId,
          sessionId: session.sessionId,
          questionId: question.id,
          ...(selectionKey ? { selectionKey } : {}),
        },
      });
    }
    actions.push({
      tag: "button",
      text: { tag: "plain_text", content: "转回本机回答" },
      value: {
        action: "input_local",
        requestId,
        sessionId: session.sessionId,
      },
    });

    if (question.options.length === 0) {
      elements.push({
        tag: "note",
        elements: [{
          tag: "plain_text",
          content: `请引用本卡片回复文字${question.custom === false ? "（当前问题没有可用选项）" : "，或转回本机回答"}。`,
        }],
      });
    } else if (question.multiple) {
      elements.push({
        tag: "note",
        elements: [{
          tag: "plain_text",
          content: selected.length > 0
            ? `已选 ${selected.length} 项；继续点击可切换选择，完成后点击“提交选择”。`
            : "点击选项进行多选，完成后点击“提交选择”。",
        }],
      });
    } else {
      elements.push({
        tag: "note",
        elements: [{
          tag: "plain_text",
          content: question.custom === false
            ? "点击一个选项即可提交；也可以引用本卡片重新选择。"
            : "点击一个选项即可提交；也可以引用本卡片回复自定义答案。",
        }],
      });
    }
    for (const row of chunkCardActions(actions)) {
      elements.push({ tag: "action", actions: row });
    }
  }

  return {
    config: { wide_screen_mode: true, update_multi: true },
    header: {
      template: answered ? "green" : "orange",
      title: {
        tag: "plain_text",
        content: answered
          ? `${runtime} 已记录第 ${questionIndex + 1} 个问题`
          : `${runtime} 等待你回答（${questionIndex + 1}/${questionCount}）`,
      },
    },
    elements,
  };
}

export function buildResolvedUserInputQuestionCard(
  session: SessionRecord,
  question: UserInputQuestion,
  answers: string[] | undefined,
  resolution: "answered" | "local" | "timeout" | "rejected",
  questionIndex: number,
  questionCount: number,
): Card {
  const runtime = runtimeDisplayName(session.runtime);
  const result = resolution === "answered"
    ? question.isSecret
      ? "已提供（已隐藏）"
      : truncate((answers ?? []).join("、") || "（空）", 500)
    : resolution === "local"
      ? `已转回电脑端，请在原 ${runtime} 窗口回答。`
      : resolution === "rejected"
        ? `已在原 ${runtime} 窗口取消这组问题。`
        : "飞书回答已超时，已转回电脑端。";
  return {
    config: { wide_screen_mode: true, update_multi: true },
    header: {
      template: resolution === "answered" ? "green" : "grey",
      title: {
        tag: "plain_text",
        content: `${runtime} 补充信息${resolution === "answered" ? "已提交" : "已处理"}（${questionIndex + 1}/${questionCount}）`,
      },
    },
    elements: [
      {
        tag: "div",
        text: {
          tag: "lark_md",
          content: `**会话：** ${sessionLabel(session)}\n**${truncate(question.header, 80)}**\n${truncate(question.question, 800)}\n\n**结果：** ${result}`,
        },
      },
    ],
  };
}

function chunkCardActions(
  actions: Record<string, unknown>[],
): Record<string, unknown>[][] {
  const rows: Record<string, unknown>[][] = [];
  for (let index = 0; index < actions.length; index += 3) {
    rows.push(actions.slice(index, index + 3));
  }
  return rows;
}

export function buildActivityCard(
  session: SessionRecord,
  events: ActivityCardEvent[],
  startedAt: string,
  completed = false,
): Card {
  const runtime = runtimeDisplayName(session.runtime);
  const eventElements = events.flatMap((event) => [
    {
      tag: "div",
      text: {
        tag: "lark_md",
        content: `**${formatActivityTime(event.at)}　${event.label}**`,
      },
    },
    ...(event.detail
      ? markdownToFeishuCardElements(redactSensitiveText(event.detail), {
          maxCharacters: 500,
          maxElements: 2,
        })
      : []),
  ]);
  return {
    config: { wide_screen_mode: true, update_multi: true },
    header: {
      template: completed ? "green" : "blue",
      title: {
        tag: "plain_text",
        content: completed ? `${runtime} 本轮处理完成` : `${runtime} 正在处理`,
      },
    },
    elements: [
      {
        tag: "div",
        text: {
          tag: "lark_md",
          content: `**会话：** ${sessionLabel(session)}\n**开始：** ${formatActivityTime(startedAt)}\n**目录：** ${session.cwd}`,
        },
      },
      { tag: "hr" },
      ...(eventElements.length > 0
        ? eventElements
        : [{ tag: "div", text: { tag: "plain_text", content: "正在准备任务…" } }]),
      ...(!completed
        ? [
            {
              tag: "note",
              elements: [
                {
                  tag: "plain_text",
                  content: "同一轮只保留一张进度卡，工具执行与上下文压缩会在这里更新。",
                },
              ],
            },
          ]
        : []),
    ],
  };
}

export function buildStopCard(
  session: SessionRecord,
  assistantMessage: string,
): Card {
  return buildStopCards(session, assistantMessage)[0]!;
}

export function buildStopCards(
  session: SessionRecord,
  assistantMessage: string,
): Card[] {
  const runtime = runtimeDisplayName(session.runtime);
  const safeMessage = redactSensitiveText(assistantMessage).trim();
  const waitingForReply = looksLikeQuestion(safeMessage);
  const continuationHint = session.managedByAssistant === true
    ? "下一轮请直接发送消息。"
    : "这个窗口不是由 AI CLI 飞书助手打开，不能从飞书回复。";
  const chunks = splitTextForFeishu(safeMessage || `${runtime} 已结束本轮处理。`, 2_800);
  return chunks.map((chunk, index) => buildMessageCard({
    session,
    text: chunk,
    title: waitingForReply ? `${runtime} 等待你回复` : `${runtime} 本轮已完成`,
    template: waitingForReply ? "orange" : "green",
    sectionTitle: `${runtime} 回复`,
    partIndex: index,
    partCount: chunks.length,
    footer: index === chunks.length - 1 ? continuationHint : undefined,
  }));
}

export function buildUserPromptCards(
  session: SessionRecord,
  prompt: string,
): Card[] {
  const chunks = splitTextForFeishu(redactSensitiveText(prompt), 2_800);
  return chunks.map((chunk, index) => buildMessageCard({
    session,
    text: chunk,
    title: "电脑端已提交消息",
    template: "blue",
    sectionTitle: "你的消息",
    partIndex: index,
    partCount: chunks.length,
  }));
}

export function buildErrorCard(session: SessionRecord, error: string): Card {
  return buildErrorCards(session, error)[0]!;
}

export interface ErrorCardRetryState {
  cycleId: string;
  state: "scheduled" | "running" | "stopped";
}

export function buildErrorCards(
  session: SessionRecord,
  error: string,
  retry?: ErrorCardRetryState,
): Card[] {
  const runtime = runtimeDisplayName(session.runtime);
  const chunks = splitTextForFeishu(redactSensitiveText(error), 2_800);
  return (chunks.length > 0 ? chunks : ["未知错误"]).map((chunk, index) =>
    buildMessageCard({
      session,
      text: chunk,
      title: `${runtime} 运行错误`,
      template: "red",
      sectionTitle: "错误信息",
      partIndex: index,
      partCount: Math.max(1, chunks.length),
      actions:
        index === Math.max(1, chunks.length) - 1 && retry && retry.state !== "stopped"
          ? [{
              tag: "button",
              type: "danger",
              text: {
                tag: "plain_text",
                content: retry?.state === "running"
                  ? "停止后续自动重试"
                  : "停止自动重试",
              },
              value: {
                action: "retry_stop",
                sessionId: session.sessionId,
                retryCycleId: retry?.cycleId,
              },
            }]
          : undefined,
      footer:
        index === Math.max(1, chunks.length) - 1 && retry?.state === "stopped"
          ? "已停止自动重试。你仍可以从飞书或电脑端重新发送任务。"
          : undefined,
    })
  );
}

function buildMessageCard(options: {
  session: SessionRecord;
  text: string;
  title: string;
  template: "blue" | "green" | "orange" | "red";
  sectionTitle: string;
  partIndex: number;
  partCount: number;
  footer?: string;
  actions?: Record<string, unknown>[];
}): Card {
  const messageElements = markdownToFeishuCardElements(options.text, {
    maxCharacters: Math.max(3_200, options.text.length + 1),
    maxElements: 100,
    truncate: false,
  });
  const partSuffix = options.partCount > 1
    ? `（${options.partIndex + 1}/${options.partCount}）`
    : "";
  return {
    config: { wide_screen_mode: true },
    header: {
      template: options.template,
      title: {
        tag: "plain_text",
        content: `${options.title}${partSuffix}`,
      },
    },
    elements: [
      {
        tag: "div",
        text: {
          tag: "lark_md",
          content: `**会话：** ${sessionLabel(options.session)}\n**项目：** ${options.session.projectName}`,
        },
      },
      { tag: "hr" },
      {
        tag: "div",
        text: {
          tag: "lark_md",
          content: `**${options.sectionTitle}${partSuffix}**`,
        },
      },
      ...(messageElements.length > 0
        ? messageElements
        : [{
            tag: "div",
            text: { tag: "plain_text", content: options.text || "（空）" },
          }]),
      ...(options.footer
        ? [{
            tag: "note",
            elements: [{ tag: "plain_text", content: options.footer }],
          }]
        : []),
      ...(options.actions && options.actions.length > 0
        ? [{ tag: "action", actions: options.actions }]
        : []),
    ],
  };
}

function formatActivityTime(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }
  return new Intl.DateTimeFormat("zh-CN", {
    timeZone: "Asia/Shanghai",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
  }).format(date);
}
