import type {
  ApprovalRecord,
  ApprovalResolution,
  SessionRecord,
  UserInputQuestion,
} from "./domain.js";
import {
  looksLikeQuestion,
  redactSensitiveText,
  sessionAddress,
  sessionLabel,
  truncate,
} from "./domain.js";

type Card = Record<string, unknown>;

export interface ActivityCardEvent {
  at: string;
  label: string;
  detail?: string;
}

function approvalResultText(resolution: ApprovalResolution): string {
  switch (resolution) {
    case "allow":
      return "已批准，Codex 将继续执行。";
    case "deny":
      return "已拒绝，Codex 会收到拒绝结果。";
    case "local":
      return "已转回电脑端，请在原 Codex 窗口确认。";
    case "timeout":
      return "飞书审批已超时，已转回电脑端确认。";
  }
}

export function buildApprovalCard(
  session: SessionRecord,
  approval: ApprovalRecord,
): Card {
  const detail = truncate(approval.toolPreview || "（没有可展示的参数）", 2600);
  return {
    config: { wide_screen_mode: true, update_multi: true },
    header: {
      template: "orange",
      title: { tag: "plain_text", content: "Codex 需要你的确认" },
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
            content: "只批准这一次。若信息不足，可转回电脑端查看完整上下文。",
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
            text: { tag: "plain_text", content: "转回本机确认" },
            value: {
              action: "approval_local",
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
  const template = resolution === "allow" ? "green" : resolution === "deny" ? "red" : "grey";
  return {
    config: { wide_screen_mode: true, update_multi: true },
    header: {
      template,
      title: { tag: "plain_text", content: "Codex 审批已处理" },
    },
    elements: [
      {
        tag: "div",
        text: {
          tag: "lark_md",
          content: `**会话：** ${sessionLabel(session)}\n**工具：** ${approval.toolName}\n\n${approvalResultText(resolution)}`,
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
      title: { tag: "plain_text", content: "Codex 等待你补充信息" },
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
  const result = resolution === "answered"
    ? questions
        .map(
            (question, index) =>
            `${index + 1}. **${truncate(question.header, 60)}：** ${question.isSecret ? "已提供（已隐藏）" : truncate(answers?.[question.id] ?? "", 300)}`,
        )
        .join("\n")
    : resolution === "local"
      ? "已转回电脑端，请在原 Codex 窗口回答。"
      : "飞书回答已超时，已转回电脑端。";
  return {
    config: { wide_screen_mode: true, update_multi: true },
    header: {
      template: resolution === "answered" ? "green" : "grey",
      title: { tag: "plain_text", content: "Codex 补充信息已处理" },
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
  const eventText = events
    .map((event) => {
      const detail = event.detail ? `\n> ${truncate(redactSensitiveText(event.detail), 500)}` : "";
      return `- ${formatActivityTime(event.at)}　${event.label}${detail}`;
    })
    .join("\n");
  return {
    config: { wide_screen_mode: true, update_multi: true },
    header: {
      template: completed ? "green" : "blue",
      title: {
        tag: "plain_text",
        content: completed ? "Codex 本轮处理完成" : "Codex 正在处理",
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
      {
        tag: "div",
        text: {
          tag: "lark_md",
          content: eventText || "正在准备任务…",
        },
      },
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
  const safeMessage = truncate(redactSensitiveText(assistantMessage), 3200);
  const waitingForReply = looksLikeQuestion(safeMessage);
  const replyHint = session.alias
    ? `引用回复本消息即可继续；也可发送 @${session.alias} 回复内容，或 #${session.shortId} 回复内容。`
    : `引用回复本消息即可继续；也可发送 ${sessionAddress(session)} 回复内容。`;
  return {
    config: { wide_screen_mode: true },
    header: {
      template: waitingForReply ? "orange" : "green",
      title: {
        tag: "plain_text",
        content: waitingForReply ? "Codex 等待你回复" : "Codex 本轮已完成",
      },
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
      {
        tag: "div",
        text: {
          tag: "lark_md",
          content: safeMessage || "Codex 已结束本轮处理。",
        },
      },
      {
        tag: "note",
        elements: [
          {
            tag: "plain_text",
            content: replyHint,
          },
        ],
      },
    ],
  };
}

export function buildErrorCard(session: SessionRecord, error: string): Card {
  return {
    config: { wide_screen_mode: true },
    header: {
      template: "red",
      title: { tag: "plain_text", content: "Codex 远程继续失败" },
    },
    elements: [
      {
        tag: "div",
        text: {
          tag: "lark_md",
          content: `**会话：** ${sessionLabel(session)}\n**原因：** ${truncate(redactSensitiveText(error), 1200)}\n\n请回到电脑端查看详细信息。`,
        },
      },
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
