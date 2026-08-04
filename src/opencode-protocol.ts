export type OpenCodePermissionResponse = "once" | "always" | "reject";

export interface OpenCodeModel {
  id?: string;
  modelID?: string;
  providerID?: string;
  variant?: string;
}

export interface OpenCodeSession {
  id: string;
  title?: string;
  directory?: string;
  worktree?: string | null;
  parentID?: string;
  model?: string | OpenCodeModel;
  agent?: string;
  version?: string | number;
  time?: {
    created?: number;
    updated?: number;
  };
}

export interface OpenCodeMessagePart {
  id?: string;
  sessionID?: string;
  messageID?: string;
  type: string;
  text?: string;
  tool?: string;
  state?: {
    status?: "pending" | "running" | "completed" | "error";
    input?: unknown;
    output?: unknown;
    title?: string;
  };
}

export interface OpenCodeMessage {
  id: string;
  role: "system" | "user" | "assistant";
  parts?: OpenCodeMessagePart[];
  sessionID?: string;
  info?: Record<string, unknown>;
  time?: {
    created?: number;
    completed?: number;
  };
}

export interface OpenCodePermission {
  id: string;
  sessionID?: string;
  action?: string;
  resources?: string[];
  save?: string[];
  type?: string;
  description?: string;
  input?: unknown;
  permission?: string | {
    allow?: boolean;
    ask?: boolean;
    deny?: boolean;
    option?: string;
  };
  patterns?: string[];
  metadata?: Record<string, unknown>;
  always?: string[];
  tool?: {
    messageID?: string;
    callID?: string;
  };
  source?: {
    type?: string;
    messageID?: string;
    callID?: string;
  };
  time?: {
    created?: number;
    updated?: number;
  };
}

export interface OpenCodeQuestionOption {
  label: string;
  description: string;
}

export interface OpenCodeQuestion {
  question: string;
  header: string;
  options: OpenCodeQuestionOption[];
  multiple?: boolean;
  custom?: boolean;
}

export interface OpenCodeQuestionRequest {
  id: string;
  sessionID: string;
  questions: OpenCodeQuestion[];
  tool?: {
    messageID?: string;
    callID?: string;
  };
}

export interface OpenCodeQuestionReplied {
  sessionID: string;
  requestID: string;
  answers: string[][];
}

export interface OpenCodeQuestionRejected {
  sessionID: string;
  requestID: string;
}

export interface OpenCodePermissionReplied {
  sessionID: string;
  requestID: string;
  reply: OpenCodePermissionResponse;
}

export interface OpenCodeEvent {
  type: string;
  properties: Record<string, unknown>;
}

export interface OpenCodeMessagePartUpdatedProperties {
  sessionID?: string;
  messageID?: string;
  message?: OpenCodeMessage;
  part?: OpenCodeMessagePart;
}

export interface OpenCodeEventHandlers {
  onEvent?: (event: OpenCodeEvent) => void;
  onPermissionAsked?: (permission: OpenCodePermission) => void;
  onPermissionUpdated?: (permission: OpenCodePermission) => void;
  onPermissionReplied?: (permission: OpenCodePermissionReplied) => void;
  onQuestionAsked?: (request: OpenCodeQuestionRequest) => void;
  onQuestionReplied?: (reply: OpenCodeQuestionReplied) => void;
  onQuestionRejected?: (rejection: OpenCodeQuestionRejected) => void;
  onSessionCreated?: (session: OpenCodeSession) => void;
  onSessionUpdated?: (session: OpenCodeSession) => void;
  onSessionIdle?: (sessionID: string) => void;
  onSessionError?: (sessionID: string, error?: string) => void;
  onSessionDeleted?: (sessionID: string) => void;
  onSessionStatus?: (sessionID: string, status: string) => void;
  onSessionCompacted?: (sessionID: string) => void;
  onMessagePartUpdated?: (properties: OpenCodeMessagePartUpdatedProperties) => void;
  onMessageUpdated?: (message: OpenCodeMessage) => void;
  onDisconnected?: (error: unknown) => void;
}

export function isObject(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

export function asOpenCodeSession(value: unknown): OpenCodeSession | undefined {
  if (!isObject(value) || typeof value.id !== "string") {
    return undefined;
  }
  return value as unknown as OpenCodeSession;
}

export function asOpenCodePermission(value: unknown): OpenCodePermission | undefined {
  if (!isObject(value)) {
    return undefined;
  }
  const candidate = isObject(value.data) ? value.data : value;
  if (!isObject(candidate) || typeof candidate.id !== "string") {
    return undefined;
  }
  const sessionID = [candidate.sessionID, candidate.sessionId, candidate.session_id]
    .find((item): item is string => typeof item === "string" && item.length > 0);
  const strings = (input: unknown): string[] | undefined =>
    Array.isArray(input)
      ? input.filter((item): item is string => typeof item === "string")
      : undefined;
  const resources = strings(candidate.resources);
  const save = strings(candidate.save);
  const patterns = strings(candidate.patterns);
  const always = strings(candidate.always);
  const source = isObject(candidate.source)
    ? {
        ...(typeof candidate.source.type === "string"
          ? { type: candidate.source.type }
          : {}),
        ...(typeof candidate.source.messageID === "string"
          ? { messageID: candidate.source.messageID }
          : {}),
        ...(typeof candidate.source.callID === "string"
          ? { callID: candidate.source.callID }
          : {}),
      }
    : undefined;
  const tool = isObject(candidate.tool)
    ? {
        ...(typeof candidate.tool.messageID === "string"
          ? { messageID: candidate.tool.messageID }
          : {}),
        ...(typeof candidate.tool.callID === "string"
          ? { callID: candidate.tool.callID }
          : {}),
      }
    : source
      ? {
          ...(source.messageID ? { messageID: source.messageID } : {}),
          ...(source.callID ? { callID: source.callID } : {}),
        }
      : undefined;
  return {
    id: candidate.id,
    ...(sessionID ? { sessionID } : {}),
    ...(typeof candidate.action === "string" ? { action: candidate.action } : {}),
    ...(resources ? { resources } : {}),
    ...(save ? { save } : {}),
    ...(typeof candidate.type === "string" ? { type: candidate.type } : {}),
    ...(typeof candidate.description === "string"
      ? { description: candidate.description }
      : {}),
    ...(candidate.input !== undefined ? { input: candidate.input } : {}),
    ...(typeof candidate.permission === "string" || isObject(candidate.permission)
      ? { permission: candidate.permission as OpenCodePermission["permission"] }
      : {}),
    ...(patterns ? { patterns } : {}),
    ...(isObject(candidate.metadata) ? { metadata: candidate.metadata } : {}),
    ...(always ? { always } : {}),
    ...(tool ? { tool } : {}),
    ...(source ? { source } : {}),
    ...(isObject(candidate.time) ? { time: candidate.time } : {}),
  };
}

export function openCodePermissionList(value: unknown): OpenCodePermission[] {
  const items = Array.isArray(value)
    ? value
    : isObject(value) && Array.isArray(value.data)
      ? value.data
      : [];
  return items
    .map(asOpenCodePermission)
    .filter((item): item is OpenCodePermission => Boolean(item));
}

export function asOpenCodeQuestionRequest(
  value: unknown,
): OpenCodeQuestionRequest | undefined {
  if (
    !isObject(value) ||
    typeof value.id !== "string" ||
    typeof value.sessionID !== "string" ||
    !Array.isArray(value.questions)
  ) {
    return undefined;
  }
  const questions: OpenCodeQuestion[] = [];
  for (const question of value.questions) {
    if (
      !isObject(question) ||
      typeof question.question !== "string" ||
      typeof question.header !== "string" ||
      !Array.isArray(question.options)
    ) {
      return undefined;
    }
    const options: OpenCodeQuestionOption[] = [];
    for (const option of question.options) {
      if (!isObject(option) || typeof option.label !== "string") {
        return undefined;
      }
      options.push({
        label: option.label,
        description: typeof option.description === "string" ? option.description : "",
      });
    }
    questions.push({
      question: question.question,
      header: question.header,
      options,
      multiple: question.multiple === true,
      custom: question.custom !== false,
    });
  }
  return {
    id: value.id,
    sessionID: value.sessionID,
    questions,
    ...(isObject(value.tool) ? { tool: value.tool } : {}),
  };
}

export function asOpenCodeQuestionReplied(
  value: unknown,
): OpenCodeQuestionReplied | undefined {
  if (
    !isObject(value) ||
    typeof value.sessionID !== "string" ||
    typeof value.requestID !== "string" ||
    !Array.isArray(value.answers)
  ) {
    return undefined;
  }
  const answers = value.answers.map((answer) =>
    Array.isArray(answer)
      ? answer.filter((item): item is string => typeof item === "string")
      : [],
  );
  return { sessionID: value.sessionID, requestID: value.requestID, answers };
}

export function asOpenCodeQuestionRejected(
  value: unknown,
): OpenCodeQuestionRejected | undefined {
  if (
    !isObject(value) ||
    typeof value.sessionID !== "string" ||
    typeof value.requestID !== "string"
  ) {
    return undefined;
  }
  return { sessionID: value.sessionID, requestID: value.requestID };
}

export function asOpenCodePermissionReplied(
  value: unknown,
): OpenCodePermissionReplied | undefined {
  const candidate = isObject(value) && isObject(value.data) ? value.data : value;
  if (
    !isObject(candidate) ||
    typeof candidate.sessionID !== "string" ||
    typeof candidate.requestID !== "string" ||
    !["once", "always", "reject"].includes(String(candidate.reply))
  ) {
    return undefined;
  }
  return {
    sessionID: candidate.sessionID,
    requestID: candidate.requestID,
    reply: candidate.reply as OpenCodePermissionResponse,
  };
}

export function asOpenCodeMessage(value: unknown): OpenCodeMessage | undefined {
  if (!isObject(value) || typeof value.id !== "string") {
    return undefined;
  }
  return value as unknown as OpenCodeMessage;
}

export function openCodeSessionId(properties: Record<string, unknown>): string | undefined {
  if (typeof properties.sessionID === "string") {
    return properties.sessionID;
  }
  if (isObject(properties.info) && typeof properties.info.id === "string") {
    return properties.info.id;
  }
  return undefined;
}

export function extractOpenCodeSession(
  properties: Record<string, unknown>,
): OpenCodeSession | undefined {
  if (isObject(properties.info)) {
    return asOpenCodeSession(properties.info);
  }
  return asOpenCodeSession(properties);
}

export function extractOpenCodeMessage(
  properties: Record<string, unknown>,
): OpenCodeMessage | undefined {
  if (isObject(properties.info)) {
    const message = asOpenCodeMessage(properties.info);
    if (message && Array.isArray(properties.parts)) {
      return {
        ...message,
        parts: properties.parts as unknown as OpenCodeMessagePart[],
      };
    }
    return message;
  }
  return asOpenCodeMessage(properties);
}

export function extractOpenCodeError(value: unknown): string | undefined {
  if (typeof value === "string") {
    return value;
  }
  if (isObject(value)) {
    if (typeof value.message === "string") {
      return value.message;
    }
    if (isObject(value.data) && typeof value.data.message === "string") {
      return value.data.message;
    }
    return JSON.stringify(value);
  }
  return undefined;
}

export function openCodeStatusName(value: unknown): string | undefined {
  if (typeof value === "string") {
    return value;
  }
  if (isObject(value) && typeof value.type === "string") {
    return value.type;
  }
  return undefined;
}
