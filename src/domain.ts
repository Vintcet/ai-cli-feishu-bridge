import path from "node:path";

export type SessionStatus =
  | "starting"
  | "ready"
  | "running"
  | "waiting"
  | "pending_approval"
  | "pending_input"
  | "local_approval"
  | "error"
  | "ended";

export type RuntimeName = "codex" | "opencode" | "claudecode";
export type ManagedRuntimeName = Exclude<RuntimeName, "opencode">;
export type RuntimeTransport = "managed_terminal" | "http_event_stream";

export interface RuntimeDefinition {
  name: RuntimeName;
  displayName: string;
  shortName: string;
  groupPrefix: string;
  transport: RuntimeTransport;
}

const runtimeDefinitions: Record<RuntimeName, RuntimeDefinition> = {
  codex: {
    name: "codex",
    displayName: "Codex",
    shortName: "Codex",
    groupPrefix: "Codex｜",
    transport: "managed_terminal",
  },
  claudecode: {
    name: "claudecode",
    displayName: "Claude Code",
    shortName: "Claude",
    groupPrefix: "Claude｜",
    transport: "managed_terminal",
  },
  opencode: {
    name: "opencode",
    displayName: "opencode",
    shortName: "opencode",
    groupPrefix: "OpenCode｜",
    transport: "http_event_stream",
  },
};

export function isRuntimeName(value: unknown): value is RuntimeName {
  return typeof value === "string" &&
    Object.prototype.hasOwnProperty.call(runtimeDefinitions, value);
}

export function isManagedRuntimeName(value: unknown): value is ManagedRuntimeName {
  return isRuntimeName(value) &&
    runtimeDefinitions[value].transport === "managed_terminal";
}

export function runtimeDefinition(runtime?: RuntimeName): RuntimeDefinition {
  return runtimeDefinitions[runtime ?? "codex"];
}

export interface BridgeSettings {
  workspaceRoot: string;
  notifyActivity: boolean;
  notifyUserPrompts: boolean;
  autoRetryErrors: boolean;
  retryMaxAttempts: number;
  retryIntervalSeconds: number;
  retryJitterSeconds: number;
  autoApprove: boolean;
  notifyAutoApprovals: boolean;
}

export interface Binding {
  openId: string;
  chatId: string;
  chatType: string;
  boundAt: string;
}

export interface BindingStore {
  users: Record<string, Binding>;
  ownerOpenId?: string;
  pairingCode?: string;
}

export interface SessionRecord {
  sessionId: string;
  shortId: string;
  alias?: string;
  cwd: string;
  projectName: string;
  model?: string;
  status: SessionStatus;
  openedAt: string;
  lastSeenAt: string;
  lastTurnId?: string;
  lastAssistantMessage?: string;
  lastNotificationTurnId?: string;
  lastNotificationStatus?: "pending" | "sent";
  pendingNotificationKind?: "stop" | "error";
  pendingNotificationMessage?: string;
  lastError?: string;
  source?: string;
  runtime?: RuntimeName;
  endedAt?: string;
  clientProcessId?: number;
  clientProcessStartedAt?: string;
  managedTerminalId?: string;
  managedTerminalElevated?: boolean;
  managedByAssistant?: boolean;
  transcriptPath?: string;
  historyHiddenAt?: string;
  feishuChatId?: string;
  feishuChatName?: string;
  feishuChatCreatedAt?: string;
  feishuChatError?: string;
  feishuChatErrorAt?: string;
}

export interface SessionStore {
  sessions: Record<string, SessionRecord>;
}

export type MessageRouteKind =
  | "stop"
  | "approval"
  | "input"
  | "activity"
  | "user_prompt"
  | "resume_ack"
  | "error";

export interface MessageRoute {
  messageId: string;
  sessionId: string;
  requestId?: string;
  chatId: string;
  kind: MessageRouteKind;
  createdAt: string;
}

export interface RouteStore {
  messages: Record<string, MessageRoute>;
  processedInbound: Record<string, string>;
}

export type ApprovalResolution = "allow" | "deny" | "local" | "timeout";
export type ApprovalRiskLevel = "low" | "high";

export interface ApprovalRecord {
  requestId: string;
  sessionId: string;
  turnId: string;
  cwd: string;
  toolName: string;
  toolPreview: string;
  createdAt: string;
  expiresAt: string;
  status: "pending" | "resolved" | "orphaned";
  resolution?: ApprovalResolution;
  resolvedAt?: string;
  messageIds: string[];
  requiresManualApproval?: boolean;
  riskLevel?: ApprovalRiskLevel;
  riskReason?: string;
  opencodePermissionId?: string;
}

export interface ApprovalStore {
  requests: Record<string, ApprovalRecord>;
}

export interface PermissionHookPayload {
  hook_event_name: "PermissionRequest";
  session_id: string;
  turn_id: string;
  cwd: string;
  model: string;
  permission_mode: string;
  tool_name: string;
  tool_input: unknown;
  transcript_path: string | null;
  runtime?: RuntimeName;
  agent_id?: string;
  agent_type?: string;
  managed_terminal_id?: string;
  managed_terminal_elevated?: boolean;
}

export interface StopHookPayload {
  hook_event_name: "Stop";
  session_id: string;
  turn_id: string;
  cwd: string;
  model: string;
  permission_mode: string;
  last_assistant_message: string | null;
  stop_hook_active: boolean;
  transcript_path: string | null;
  runtime?: RuntimeName;
  managed_terminal_id?: string;
  managed_terminal_elevated?: boolean;
}

export interface UserInputOption {
  label: string;
  description: string;
  preview?: string;
}

export interface UserInputQuestion {
  header: string;
  id: string;
  question: string;
  options: UserInputOption[];
  isSecret?: boolean;
  multiple?: boolean;
  custom?: boolean;
}

export type UserInputAnswers = Record<string, string[]>;

export interface RequestUserInputHookPayload {
  hook_event_name: "PreToolUse";
  session_id: string;
  turn_id: string;
  cwd: string;
  model?: string;
  permission_mode?: string;
  tool_name: "request_user_input";
  tool_input: {
    questions: UserInputQuestion[];
    autoResolutionMs?: number;
    claudeCodeOriginalInput?: Record<string, unknown>;
    claudeCodeQuestionTextById?: Record<string, string>;
  };
  runtime?: RuntimeName;
  tool_use_id?: string;
  transcript_path?: string | null;
  managed_terminal_id?: string;
  managed_terminal_elevated?: boolean;
}

export type ActivityHookEventName =
  | "PreToolUse"
  | "PostToolUse"
  | "PostToolUseFailure"
  | "PreCompact"
  | "PostCompact"
  | "UserPromptSubmit";

export interface ActivityHookPayload {
  hook_event_name: ActivityHookEventName;
  session_id: string;
  turn_id?: string;
  cwd: string;
  model?: string;
  prompt?: string;
  tool_name?: string;
  tool_preview?: string;
  tool_response_preview?: string;
  runtime?: RuntimeName;
  transcript_path?: string | null;
  managed_terminal_id?: string;
  managed_terminal_elevated?: boolean;
}

export interface SessionStartHookPayload {
  hook_event_name: "SessionStart";
  session_id: string;
  cwd: string;
  model: string;
  permission_mode: string;
  source: "startup" | "resume" | "clear" | "compact";
  transcript_path: string | null;
  runtime?: RuntimeName;
  client_process_id?: number;
  client_process_started_at?: string;
  managed_terminal_id?: string;
  managed_terminal_elevated?: boolean;
}

export interface SessionEndHookPayload {
  hook_event_name: "SessionEnd";
  session_id: string;
  cwd: string;
  reason: string;
  transcript_path: string | null;
  runtime?: RuntimeName;
  managed_terminal_id?: string;
  managed_terminal_elevated?: boolean;
}

export function shortSessionId(sessionId: string): string {
  const compact = sessionId.replace(/[^a-zA-Z0-9]/g, "");
  return compact.slice(-8).toLowerCase() || sessionId.slice(-8).toLowerCase();
}

export function stringifyModel(value: unknown): string {
  if (typeof value === "string") {
    return value;
  }
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return "";
  }
  const item = value as Record<string, unknown>;
  if (typeof item.id === "string" && item.id) {
    return typeof item.providerID === "string" &&
      item.providerID !== "opencode" &&
      item.providerID
      ? `${item.providerID}/${item.id}`
      : item.id;
  }
  try {
    return JSON.stringify(value);
  } catch {
    return String(value);
  }
}

export function projectNameFromCwd(cwd: string): string {
  const normalized = path.resolve(cwd);
  return path.basename(normalized) || normalized;
}

export const sessionAliasMaxLength = 20;

export function normalizeSessionAlias(value: string): string {
  return value.trim().normalize("NFC");
}

export function sessionAliasKey(value: string): string {
  return normalizeSessionAlias(value).toLocaleLowerCase("en-US");
}

export function sessionAliasValidationError(value: string): string | undefined {
  const alias = normalizeSessionAlias(value);
  if (!alias) {
    return "别名不能为空。";
  }
  if (Array.from(alias).length > sessionAliasMaxLength) {
    return `别名最多 ${sessionAliasMaxLength} 个字符。`;
  }
  if (!/^[\p{L}\p{N}_-]+$/u.test(alias)) {
    return "别名只能包含中文、字母、数字、下划线或短横线，不能包含空格。";
  }
  return undefined;
}

export function sessionAddress(
  session: Pick<SessionRecord, "alias" | "shortId">,
): string {
  return session.alias ? `@${session.alias}` : `#${session.shortId}`;
}

export function sessionLabel(
  session: Pick<SessionRecord, "alias" | "projectName" | "shortId">,
): string {
  return session.alias
    ? `@${session.alias} · ${session.projectName} #${session.shortId}`
    : `${session.projectName} #${session.shortId}`;
}

export function runtimeDisplayName(runtime?: RuntimeName): string {
  return runtimeDefinition(runtime).displayName;
}

export function runtimeGroupPrefix(runtime?: RuntimeName): string {
  return runtimeDefinition(runtime).groupPrefix;
}

export function runtimeReceivedText(runtime?: RuntimeName): string {
  return `${runtimeDisplayName(runtime)} 已接收。`;
}

export function isPermissionHookPayload(value: unknown): value is PermissionHookPayload {
  if (!value || typeof value !== "object") {
    return false;
  }
  const item = value as Record<string, unknown>;
  return (
    item.hook_event_name === "PermissionRequest" &&
    typeof item.session_id === "string" &&
    typeof item.turn_id === "string" &&
    typeof item.cwd === "string" &&
    typeof item.model === "string" &&
    typeof item.tool_name === "string"
  );
}

export function isStopHookPayload(value: unknown): value is StopHookPayload {
  if (!value || typeof value !== "object") {
    return false;
  }
  const item = value as Record<string, unknown>;
  return (
    item.hook_event_name === "Stop" &&
    typeof item.session_id === "string" &&
    typeof item.turn_id === "string" &&
    typeof item.cwd === "string" &&
    typeof item.model === "string" &&
    (typeof item.last_assistant_message === "string" ||
      item.last_assistant_message === null)
  );
}

export function isRequestUserInputHookPayload(
  value: unknown,
): value is RequestUserInputHookPayload {
  if (!value || typeof value !== "object") {
    return false;
  }
  const item = value as Record<string, unknown>;
  if (
    item.hook_event_name !== "PreToolUse" ||
    item.tool_name !== "request_user_input" ||
    typeof item.session_id !== "string" ||
    typeof item.turn_id !== "string" ||
    typeof item.cwd !== "string" ||
    !item.tool_input ||
    typeof item.tool_input !== "object" ||
    Array.isArray(item.tool_input)
  ) {
    return false;
  }
  const input = item.tool_input as Record<string, unknown>;
  if (!Array.isArray(input.questions) || input.questions.length === 0) {
    return false;
  }
  return input.questions.every((question) => {
    if (!question || typeof question !== "object" || Array.isArray(question)) {
      return false;
    }
    const typed = question as Record<string, unknown>;
    const rawOptions = typed.options;
    if (rawOptions === undefined || rawOptions === null) {
      typed.options = [];
    }
    return (
      typeof typed.header === "string" &&
      typeof typed.id === "string" &&
      typeof typed.question === "string" &&
      Array.isArray(typed.options) &&
      (typed.multiple === undefined || typeof typed.multiple === "boolean") &&
      (typed.custom === undefined || typeof typed.custom === "boolean") &&
      typed.options.every(
        (option) =>
          Boolean(option) &&
          typeof option === "object" &&
          !Array.isArray(option) &&
          typeof (option as Record<string, unknown>).label === "string" &&
          typeof (option as Record<string, unknown>).description === "string" &&
          ((option as Record<string, unknown>).preview === undefined ||
            typeof (option as Record<string, unknown>).preview === "string"),
      )
    );
  });
}

export function isActivityHookPayload(value: unknown): value is ActivityHookPayload {
  if (!value || typeof value !== "object") {
    return false;
  }
  const item = value as Record<string, unknown>;
  return (
    [
      "PreToolUse",
      "PostToolUse",
      "PostToolUseFailure",
      "PreCompact",
      "PostCompact",
      "UserPromptSubmit",
    ].includes(
      String(item.hook_event_name),
    ) &&
    typeof item.session_id === "string" &&
    typeof item.cwd === "string" &&
    (item.prompt === undefined || typeof item.prompt === "string")
  );
}

export function isSessionStartHookPayload(
  value: unknown,
): value is SessionStartHookPayload {
  if (!value || typeof value !== "object") {
    return false;
  }
  const item = value as Record<string, unknown>;
  return (
    item.hook_event_name === "SessionStart" &&
    typeof item.session_id === "string" &&
    typeof item.cwd === "string" &&
    typeof item.model === "string" &&
    (item.client_process_id === undefined ||
      (typeof item.client_process_id === "number" &&
        Number.isSafeInteger(item.client_process_id) &&
        item.client_process_id > 0)) &&
    (item.client_process_started_at === undefined ||
      typeof item.client_process_started_at === "string") &&
    ["startup", "resume", "clear", "compact"].includes(String(item.source))
  );
}

export function isSessionEndHookPayload(value: unknown): value is SessionEndHookPayload {
  if (!value || typeof value !== "object") {
    return false;
  }
  const item = value as Record<string, unknown>;
  return (
    item.hook_event_name === "SessionEnd" &&
    typeof item.session_id === "string" &&
    typeof item.cwd === "string" &&
    typeof item.reason === "string" &&
    item.reason.length > 0
  );
}

const sensitiveKeyPattern =
  /(?:secret|token|password|passwd|api[_-]?key|authorization|cookie|private[_-]?key)/i;

function redactValue(key: string, value: unknown): unknown {
  if (sensitiveKeyPattern.test(key)) {
    return "[已隐藏]";
  }
  return value;
}

export function redactSensitiveText(text: string): string {
  return text
    .replace(
      /\b([A-Z0-9_]*(?:SECRET|TOKEN|PASSWORD|PASSWD|API_KEY)[A-Z0-9_]*)\s*=\s*([^\s"']+)/gi,
      "$1=[已隐藏]",
    )
    .replace(
      /("?(?:secret|token|password|passwd|api[_-]?key|authorization|cookie)"?\s*[:=]\s*)["']?[^\s,}"']+["']?/gi,
      "$1[已隐藏]",
    );
}

export function previewJson(value: unknown, maxLength = 2600): string {
  let text: string;
  try {
    text = JSON.stringify(value, redactValue, 2) ?? String(value);
  } catch {
    text = String(value);
  }
  return truncate(redactSensitiveText(text), maxLength);
}

export function truncate(text: string, maxLength: number): string {
  const normalized = text.trim();
  if (normalized.length <= maxLength) {
    return normalized;
  }
  return `${normalized.slice(0, Math.max(0, maxLength - 12))}\n…（已截断）`;
}

export function looksLikeQuestion(text: string): boolean {
  return /[?？]\s*$/.test(text.trim()) ||
    /(?:请|需要你|麻烦你).{0,12}(?:提供|确认|选择|补充|回复|告诉)/.test(text);
}

export function statusLabel(status: SessionStatus): string {
  switch (status) {
    case "starting":
      return "正在启动";
    case "ready":
      return "窗口已打开";
    case "running":
      return "运行中";
    case "waiting":
      return "等待回复";
    case "pending_approval":
      return "待审批";
    case "pending_input":
      return "待补充信息";
    case "local_approval":
      return "本机确认中";
    case "error":
      return "异常";
    case "ended":
      return "已结束";
  }
}
