import { readFile } from "node:fs/promises";

import { captureCodexAncestor } from "../process-tracking.js";

const defaultBridgeUrl = "http://127.0.0.1:8765";

export async function readHookInput(): Promise<unknown> {
  const chunks: Buffer[] = [];
  for await (const chunk of process.stdin) {
    chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk));
  }
  const text = Buffer.concat(chunks).toString("utf8").trim();
  return text ? JSON.parse(text) : {};
}

/**
 * Claude Code 的 hook payload 比 Codex 少几个字段：SessionStart 没有 model，
 * SessionEnd 的 reason 不是固定的 "other"，Stop 既没有 turn_id/model 也没有
 * last_assistant_message（回复正文要由桥接器从 transcript_path 读取）。
 * 这里只补齐缺失字段，不覆盖已有值，因此 Codex 自己的 payload 经过时不受影响。
 */
export function normalizeClaudeCodePayload(input: unknown): unknown {
  if (!input || typeof input !== "object" || Array.isArray(input)) {
    return input;
  }
  const item = { ...(input as Record<string, unknown>) };
  item.runtime = "claudecode";

  if (typeof item.model !== "string") {
    item.model = "claude-code";
  }

  const eventName = String(item.hook_event_name ?? "");
  if (
    [
      "PermissionRequest",
      "PreToolUse",
      "PostToolUse",
      "PostToolUseFailure",
      "Stop",
    ].includes(eventName) &&
    typeof item.turn_id !== "string"
  ) {
    const eventId = typeof item.tool_use_id === "string" && item.tool_use_id
      ? item.tool_use_id
      : `${eventName.toLowerCase()}-${Date.now()}`;
    item.turn_id = `claudecode-${String(item.session_id ?? "unknown")}-${eventId}`;
  }

  switch (eventName) {
    case "SessionStart": {
      const source = String(item.source ?? "");
      if (!["startup", "resume", "clear", "compact"].includes(source)) {
        item.source = "startup";
      }
      break;
    }
    case "SessionEnd": {
      if (typeof item.reason !== "string" || !item.reason) {
        item.reason = "other";
      }
      break;
    }
    case "Stop": {
      if (
        typeof item.last_assistant_message !== "string" &&
        item.last_assistant_message !== null
      ) {
        // 置 null，交给桥接器从 transcript_path 读取最终回复。
        item.last_assistant_message = null;
      }
      break;
    }
    case "UserPromptSubmit": {
      if (typeof item.prompt !== "string" && typeof item.user_prompt === "string") {
        item.prompt = item.user_prompt;
      }
      break;
    }
    case "PreToolUse": {
      if (item.tool_name === "AskUserQuestion") {
        normalizeClaudeCodeQuestions(item);
      }
      break;
    }
    default:
      break;
  }
  return item;
}

function normalizeClaudeCodeQuestions(item: Record<string, unknown>): void {
  if (!item.tool_input || typeof item.tool_input !== "object" || Array.isArray(item.tool_input)) {
    return;
  }
  const originalInput = { ...(item.tool_input as Record<string, unknown>) };
  if (!Array.isArray(originalInput.questions) || originalInput.questions.length === 0) {
    return;
  }
  const questionTextById: Record<string, string> = {};
  const questions = originalInput.questions.flatMap((question, index) => {
    if (!question || typeof question !== "object" || Array.isArray(question)) return [];
    const typed = question as Record<string, unknown>;
    if (typeof typed.question !== "string" || !typed.question.trim()) return [];
    const id = `claude_question_${index + 1}`;
    questionTextById[id] = typed.question;
    const options = Array.isArray(typed.options)
      ? typed.options.flatMap((option) => {
          if (!option || typeof option !== "object" || Array.isArray(option)) return [];
          const value = option as Record<string, unknown>;
          return typeof value.label === "string"
            ? [{
                label: value.label,
                description: typeof value.description === "string" ? value.description : "",
                ...(typeof value.preview === "string" ? { preview: value.preview } : {}),
              }]
            : [];
        })
      : [];
    return [{
      header: typeof typed.header === "string" && typed.header.trim()
        ? typed.header
        : `问题 ${index + 1}`,
      id,
      question: typed.question,
      options,
      multiple: typed.multiSelect === true,
      custom: true,
    }];
  });
  if (questions.length === 0) return;
  item.claude_code_tool_name = "AskUserQuestion";
  item.tool_name = "request_user_input";
  item.tool_input = {
    questions,
    claudeCodeOriginalInput: originalInput,
    claudeCodeQuestionTextById: questionTextById,
  };
}

export function addManagedTerminalMetadata(input: unknown): unknown {
  if (!input || typeof input !== "object" || Array.isArray(input)) {
    return input;
  }
  const managedTerminalId =
    process.env.AI_CLI_FEISHU_MANAGED_TERMINAL_ID?.trim();
  if (!managedTerminalId) {
    return input;
  }
  return {
    ...(input as Record<string, unknown>),
    managed_terminal_id: managedTerminalId,
    managed_terminal_elevated:
      process.env.AI_CLI_FEISHU_MANAGED_TERMINAL_ELEVATED === "1",
  };
}

export async function addClientProcessMetadata(input: unknown): Promise<unknown> {
  if (
    !input ||
    typeof input !== "object" ||
    Array.isArray(input) ||
    process.env.AI_CLI_FEISHU_MANAGED_TERMINAL_ID?.trim()
  ) {
    return input;
  }
  const client = await captureCodexAncestor();
  if (!client) {
    return input;
  }
  return {
    ...(input as Record<string, unknown>),
    client_process_id: client.processId,
    ...(client.startedAt ? { client_process_started_at: client.startedAt } : {}),
  };
}

export async function postHook(
  pathname: string,
  payload: unknown,
  timeoutMs: number,
): Promise<Record<string, unknown>> {
  const baseUrl = (
    process.env.AI_CLI_FEISHU_BRIDGE_URL ||
    defaultBridgeUrl
  ).replace(/\/$/, "");
  const controlToken = await readControlToken();
  const response = await fetch(`${baseUrl}${pathname}`, {
    method: "POST",
    headers: {
      "content-type": "application/json",
      "x-ai-cli-feishu-control-token": controlToken,
    },
    body: JSON.stringify(payload),
    signal: AbortSignal.timeout(timeoutMs),
  });
  if (!response.ok) {
    const detail = await response.text().catch(() => "");
    throw new Error(
      `Bridge hook ${pathname} returned HTTP ${response.status}${detail ? `: ${detail.slice(0, 300)}` : ""}`,
    );
  }
  const value: unknown = await response.json();
  return value && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : {};
}

async function readControlToken(): Promise<string> {
  const environmentToken = process.env.AI_CLI_FEISHU_CONTROL_TOKEN?.trim();
  if (environmentToken && /^[a-f0-9]{64}$/i.test(environmentToken)) {
    return environmentToken;
  }
  const text = await readFile(new URL("../../data/control-token.json", import.meta.url), "utf8");
  const value: unknown = JSON.parse(text);
  const token = value && typeof value === "object" && !Array.isArray(value)
    ? (value as { token?: unknown }).token
    : undefined;
  if (typeof token !== "string" || !/^[a-f0-9]{64}$/i.test(token)) {
    throw new Error("Bridge control token is missing or invalid.");
  }
  return token;
}

export function writeHookOutput(value: Record<string, unknown>): void {
  process.stdout.write(JSON.stringify(value));
}

/** 活动 Hook 的负载只保留桥接器用得到的字段，并压缩工具预览。 */
export function compactActivityPayload(value: unknown): unknown {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return value;
  }
  const item = value as Record<string, unknown>;
  return {
    hook_event_name: item.hook_event_name,
    session_id: item.session_id,
    turn_id: item.turn_id,
    cwd: item.cwd,
    model: item.model,
    prompt: item.prompt,
    tool_name: item.tool_name,
    tool_preview: compactPreview(item.tool_input),
    tool_response_preview: compactPreview(
      item.tool_response ??
        item.tool_result ??
        item.tool_output ??
        item.error ??
        item.summary,
    ),
    runtime: item.runtime,
    transcript_path: item.transcript_path,
    managed_terminal_id: item.managed_terminal_id,
    managed_terminal_elevated: item.managed_terminal_elevated,
  };
}

function compactPreview(value: unknown): string | undefined {
  if (value === undefined) return undefined;
  let text: string;
  try {
    text = JSON.stringify(value) ?? String(value);
  } catch {
    text = String(value);
  }
  return text.length <= 1_200 ? text : `${text.slice(0, 1_180)}…（已截断）`;
}
