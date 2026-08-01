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
      template: "orange",
      title: { tag: "plain_text", content: `${runtime} 需要你的确认` },
    },
    elements: [
      {
        tag: "div",
        text: {
          tag: "lark_md",
          content: `**会话：** ${sessionLabel(session)}\n**工具：** ${approval.toolName}\n**目录：** ${session.cwd}`,
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
            content: "只批准这一次。也可以在飞书助手的本机审批窗口处理，先处理的一端生效。",
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
        ],
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

export function buildUserInputCard(
  session: SessionRecord,
  requestId: string,
  questions: UserInputQuestion[],
): Card {
  const runtime = runtimeDisplayName(session.runtime);
  const questionElements = questions.flatMap((question, questionIndex) => {
    const options = question.options
      .map(
        (option, optionIndex) =>
          `${optionIndex + 1}. **${truncate(option.label, 80)}**${
            option.description ? ` — ${truncate(option.description, 180)}` : ""
          }`,
      )
      .join("\n");
    return [
      {
        tag: "div",
        text: {
          tag: "lark_md",
          content: `**${questionIndex + 1}. ${truncate(question.header, 80)}**\n${truncate(question.question, 600)}${question.isSecret ? "\n\n_此答案不会在处理结果卡片中回显。_" : ""}${options ? `\n\n${options}` : ""}`,
        },
      },
      ...(questionIndex < questions.length - 1 ? [{ tag: "hr" }] : []),
    ];
  });

  const actions: Record<string, unknown>[] = [];
  if (questions.length === 1 && questions[0]!.options.length <= 3) {
    for (const option of questions[0]!.options) {
      actions.push({
        tag: "button",
        ...(actions.length === 0 ? { type: "primary" } : {}),
        text: { tag: "plain_text", content: truncate(option.label, 40) },
        value: {
          action: "input_answer",
          requestId,
          sessionId: session.sessionId,
          questionId: questions[0]!.id,
          answer: option.label,
        },
      });
    }
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

  const replyHint = questions.length === 1
    ? "也可以引用本卡片回复选项编号、选项文字或自定义答案。"
    : `请引用本卡片，按问题顺序回复 ${questions.length} 个答案，并用中文分号“；”分隔，例如：1；2；自定义答案。`;
  return {
    config: { wide_screen_mode: true, update_multi: true },
    header: {
      template: "orange",
      title: { tag: "plain_text", content: `${runtime} 等待你补充信息` },
    },
    elements: [
      {
        tag: "div",
        text: {
          tag: "lark_md",
          content: `**会话：** ${sessionLabel(session)}\n**目录：** ${session.cwd}`,
        },
      },
      { tag: "hr" },
      ...questionElements,
      {
        tag: "note",
        elements: [{ tag: "plain_text", content: replyHint }],
      },
      { tag: "action", actions },
    ],
  };
}

export function buildResolvedUserInputCard(
  session: SessionRecord,
  questions: UserInputQuestion[],
  answers: Record<string, string> | undefined,
  resolution: "answered" | "local" | "timeout",
): Card {
  const runtime = runtimeDisplayName(session.runtime);
  const result = resolution === "answered"
    ? questions
        .map(
            (question, index) =>
            `${index + 1}. **${truncate(question.header, 60)}：** ${question.isSecret ? "已提供（已隐藏）" : truncate(answers?.[question.id] ?? "", 300)}`,
        )
        .join("\n")
    : resolution === "local"
      ? `已转回电脑端，请在原 ${runtime} 窗口回答。`
      : "飞书回答已超时，已转回电脑端。";
  return {
    config: { wide_screen_mode: true, update_multi: true },
    header: {
      template: resolution === "answered" ? "green" : "grey",
      title: { tag: "plain_text", content: `${runtime} 补充信息已处理` },
    },
    elements: [
      {
        tag: "div",
        text: {
          tag: "lark_md",
          content: `**会话：** ${sessionLabel(session)}\n\n${result}`,
        },
      },
    ],
  };
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
    : "外部会话不支持飞书输入。";
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

export function buildErrorCards(session: SessionRecord, error: string): Card[] {
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
