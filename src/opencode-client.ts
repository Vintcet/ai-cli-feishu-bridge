export type OpenCodePermissionResponse = "once" | "always" | "reject";

export interface OpenCodeSession {
  id: string;
  title?: string;
  directory?: string;
  worktree?: string | null;
  model?: string;
  agent?: string;
  version?: number;
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
  type?: string;
  description?: string;
  input?: unknown;
  permission?: {
    allow?: boolean;
    ask?: boolean;
    deny?: boolean;
    option?: string;
  };
  time?: {
    created?: number;
    updated?: number;
  };
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
  onPermissionUpdated?: (permission: OpenCodePermission) => void;
  onSessionCreated?: (session: OpenCodeSession) => void;
  onSessionIdle?: (sessionID: string) => void;
  onSessionError?: (sessionID: string, error?: string) => void;
  onSessionDeleted?: (sessionID: string) => void;
  onSessionStatus?: (sessionID: string, status: string) => void;
  onSessionCompacted?: (sessionID: string) => void;
  onMessagePartUpdated?: (properties: OpenCodeMessagePartUpdatedProperties) => void;
  onMessageUpdated?: (message: OpenCodeMessage) => void;
  onDisconnected?: (error: unknown) => void;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

export function asOpenCodeSession(value: unknown): OpenCodeSession | undefined {
  if (!isObject(value) || typeof value.id !== "string") {
    return undefined;
  }
  return value as unknown as OpenCodeSession;
}

export function asOpenCodePermission(value: unknown): OpenCodePermission | undefined {
  if (!isObject(value) || typeof value.id !== "string") {
    return undefined;
  }
  return value as unknown as OpenCodePermission;
}

export function asOpenCodeMessage(value: unknown): OpenCodeMessage | undefined {
  if (!isObject(value) || typeof value.id !== "string") {
    return undefined;
  }
  return value as unknown as OpenCodeMessage;
}

function openCodeSessionId(properties: Record<string, unknown>): string | undefined {
  if (typeof properties.sessionID === "string") {
    return properties.sessionID;
  }
  if (isObject(properties.info) && typeof properties.info.id === "string") {
    return properties.info.id;
  }
  return undefined;
}

function extractOpenCodeSession(
  properties: Record<string, unknown>,
): OpenCodeSession | undefined {
  if (isObject(properties.info)) {
    return asOpenCodeSession(properties.info);
  }
  return asOpenCodeSession(properties);
}

function extractOpenCodeMessage(
  properties: Record<string, unknown>,
): OpenCodeMessage | undefined {
  if (isObject(properties.info)) {
    return asOpenCodeMessage(properties.info);
  }
  return asOpenCodeMessage(properties);
}

function extractOpenCodeError(value: unknown): string | undefined {
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

function openCodeStatusName(value: unknown): string | undefined {
  if (typeof value === "string") {
    return value;
  }
  if (isObject(value) && typeof value.type === "string") {
    return value.type;
  }
  return undefined;
}

function responseErrorMessage(
  response: Response,
  context: string,
): string {
  return `${context}: HTTP ${response.status} ${response.statusText}`;
}

export class OpenCodeClient {
  private readonly pendingUserMessages = new Map<string, string>();

  constructor(private readonly baseUrl: string) {}

  private url(path: string): string {
    return `${this.baseUrl}${path}`;
  }

  async health(): Promise<boolean> {
    try {
      const response = await fetch(this.url("/global/health"), {
        signal: AbortSignal.timeout(3000),
      });
      if (!response.ok) {
        return false;
      }
      const body = await response.json().catch(() => ({}));
      if (!isObject(body)) {
        return true;
      }
      return body.healthy !== false && body.ok !== false;
    } catch {
      return false;
    }
  }

  async probeHealth(): Promise<{ healthy: boolean; version?: string }> {
    try {
      const response = await fetch(this.url("/global/health"), {
        signal: AbortSignal.timeout(2000),
      });
      if (!response.ok) {
        return { healthy: false };
      }
      const body = await response.json().catch(() => ({}));
      if (!isObject(body) || body.healthy !== true) {
        return { healthy: false };
      }
      const version = typeof body.version === "string" ? body.version : undefined;
      if (version !== undefined && !/^\d+\.\d+\.\d+/.test(version)) {
        return { healthy: false };
      }
      return { healthy: true, version };
    } catch {
      return { healthy: false };
    }
  }

  async currentDirectory(): Promise<string | undefined> {
    try {
      const response = await fetch(this.url("/path"), {
        signal: AbortSignal.timeout(3000),
      });
      if (!response.ok) {
        return undefined;
      }
      const body = await response.json().catch(() => ({}));
      if (!isObject(body)) {
        return undefined;
      }
      if (typeof body.directory === "string" && body.directory.length > 0) {
        return body.directory;
      }
      if (typeof body.worktree === "string" && body.worktree.length > 0) {
        return body.worktree;
      }
      return undefined;
    } catch {
      return undefined;
    }
  }

  async listSessions(): Promise<OpenCodeSession[]> {
    const response = await fetch(this.url("/session"), {
      signal: AbortSignal.timeout(10000),
    });
    if (!response.ok) {
      throw new Error(responseErrorMessage(response, "列出会话"));
    }
    const body = (await response.json()) as unknown;
    if (!Array.isArray(body)) {
      return [];
    }
    return body
      .map((item) => asOpenCodeSession(item))
      .filter((item): item is OpenCodeSession => Boolean(item));
  }

  async createSession(title?: string): Promise<OpenCodeSession> {
    const response = await fetch(this.url("/session"), {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(title ? { title } : {}),
      signal: AbortSignal.timeout(10000),
    });
    if (!response.ok) {
      throw new Error(responseErrorMessage(response, "创建会话"));
    }
    const body = (await response.json().catch(() => undefined)) as unknown;
    const session = asOpenCodeSession(body);
    if (!session) {
      throw new Error("创建会话：响应缺少会话 ID");
    }
    return session;
  }

  async sendPrompt(
    sessionId: string,
    text: string,
    options: {
      model?: string;
      agent?: string;
      noReply?: boolean;
    } = {},
  ): Promise<void> {
    const response = await fetch(
      this.url(`/session/${encodeURIComponent(sessionId)}/prompt_async`),
      {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({
          model: options.model,
          agent: options.agent,
          noReply: options.noReply,
          parts: [{ type: "text", text }],
        }),
        signal: AbortSignal.timeout(15000),
      },
    );
    if (!response.ok && response.status !== 204) {
      throw new Error(responseErrorMessage(response, "发送提示"));
    }
  }

  async replyPermission(
    sessionId: string,
    permissionId: string,
    response: OpenCodePermissionResponse,
  ): Promise<void> {
    const result = await fetch(
      this.url(
        `/session/${encodeURIComponent(sessionId)}/permissions/${encodeURIComponent(permissionId)}`,
      ),
      {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ response }),
        signal: AbortSignal.timeout(10000),
      },
    );
    if (!result.ok) {
      throw new Error(responseErrorMessage(result, "回复权限"));
    }
  }

  async abort(sessionId: string): Promise<void> {
    const response = await fetch(
      this.url(`/session/${encodeURIComponent(sessionId)}/abort`),
      {
        method: "POST",
        signal: AbortSignal.timeout(10000),
      },
    );
    if (!response.ok && response.status !== 204) {
      throw new Error(responseErrorMessage(response, "中止会话"));
    }
  }

  async undo(sessionId: string): Promise<void> {
    const response = await fetch(
      this.url(`/session/${encodeURIComponent(sessionId)}/undo`),
      {
        method: "POST",
        signal: AbortSignal.timeout(10000),
      },
    );
    if (!response.ok && response.status !== 204) {
      throw new Error(responseErrorMessage(response, "撤销会话"));
    }
  }

  async listMessages(sessionId: string, limit = 50): Promise<OpenCodeMessage[]> {
    const response = await fetch(
      this.url(
        `/session/${encodeURIComponent(sessionId)}/message?limit=${Math.max(1, limit)}`,
      ),
      {
        signal: AbortSignal.timeout(10000),
      },
    );
    if (!response.ok) {
      throw new Error(responseErrorMessage(response, "读取消息"));
    }
    const body = (await response.json()) as unknown;
    if (!Array.isArray(body)) {
      return [];
    }
    const messages: OpenCodeMessage[] = [];
    for (const item of body) {
      if (!isObject(item)) {
        continue;
      }
      const info = isObject(item.info) ? item.info : item;
      const message = asOpenCodeMessage(info);
      if (!message) {
        continue;
      }
      const parts = Array.isArray(item.parts)
        ? (item.parts as unknown as OpenCodeMessagePart[])
        : [];
      messages.push({ ...message, parts });
    }
    return messages;
  }

  extractLastAssistantText(
    messages: OpenCodeMessage[],
  ): { text: string; hasError: boolean } {
    let lastError = false;
    for (let index = messages.length - 1; index >= 0; index -= 1) {
      const message = messages[index];
      if (message.role !== "assistant") {
        continue;
      }
      const text = (message.parts ?? [])
        .filter((part) => part.type === "text" && typeof part.text === "string")
        .map((part) => part.text as string)
        .join("\n")
        .trim();
      const errorPart =
        (message.parts ?? []).some(
          (part) => part.state?.status === "error",
        ) || Boolean((message as { error?: unknown }).error);
      if (text || errorPart) {
        lastError = errorPart;
        return { text, hasError: lastError };
      }
    }
    return { text: "", hasError: lastError };
  }

  subscribe(handlers: OpenCodeEventHandlers): { close: () => void } {
    const controller = new AbortController();
    const run = async (): Promise<void> => {
      this.pendingUserMessages.clear();
      try {
        const response = await fetch(this.url("/event"), {
          signal: controller.signal,
          headers: { accept: "text/event-stream" },
        });
        if (!response.ok || !response.body) {
          throw new Error(responseErrorMessage(response, "订阅事件"));
        }
        await this.consumeEventStream(response.body, handlers, controller.signal);
        if (!controller.signal.aborted) {
          handlers.onDisconnected?.(new Error("事件流已结束"));
        }
      } catch (error) {
        if (!controller.signal.aborted) {
          handlers.onDisconnected?.(error);
        }
      } finally {
        this.pendingUserMessages.clear();
      }
    };
    void run();
    return { close: () => controller.abort() };
  }

  private async consumeEventStream(
    stream: ReadableStream<Uint8Array>,
    handlers: OpenCodeEventHandlers,
    signal: AbortSignal,
  ): Promise<void> {
    const reader = stream.getReader();
    const decoder = new TextDecoder();
    let buffer = "";
    try {
      while (true) {
        if (signal.aborted) {
          return;
        }
        const { done, value } = await reader.read();
        if (done) {
          return;
        }
        buffer += decoder.decode(value, { stream: true });
        let boundary = buffer.indexOf("\n\n");
        while (boundary >= 0) {
          const frame = buffer.slice(0, boundary);
          buffer = buffer.slice(boundary + 2);
          this.handleEventFrame(frame, handlers);
          boundary = buffer.indexOf("\n\n");
        }
      }
    } finally {
      reader.releaseLock();
    }
  }

  private handleEventFrame(
    frame: string,
    handlers: OpenCodeEventHandlers,
  ): void {
    const lines = frame.split("\n");
    let eventType: string | undefined;
    const dataChunks: string[] = [];
    for (const line of lines) {
      if (line.startsWith("event:")) {
        eventType = line.slice("event:".length).trim();
      } else if (line.startsWith("data:")) {
        dataChunks.push(line.slice("data:".length).trimStart());
      }
    }
    let properties: Record<string, unknown> = {};
    if (dataChunks.length > 0) {
      const raw = dataChunks.join("\n");
      try {
        const parsed = JSON.parse(raw) as unknown;
        if (isObject(parsed)) {
          if (typeof parsed.type === "string") {
            eventType = parsed.type;
            properties = isObject(parsed.properties) ? parsed.properties : {};
          } else {
            properties = parsed;
          }
        }
      } catch {
        properties = { data: raw };
      }
    }
    if (!eventType) {
      return;
    }
    handlers.onEvent?.({ type: eventType, properties });

    switch (eventType) {
      case "permission.updated": {
        const permission = asOpenCodePermission(properties);
        if (permission) {
          handlers.onPermissionUpdated?.(permission);
        }
        break;
      }
      case "session.created": {
        const session = extractOpenCodeSession(properties);
        if (session) {
          handlers.onSessionCreated?.(session);
        }
        break;
      }
      case "session.deleted": {
        const sessionId = openCodeSessionId(properties);
        if (sessionId) {
          handlers.onSessionDeleted?.(sessionId);
        }
        break;
      }
      case "session.idle": {
        const sessionId = openCodeSessionId(properties);
        if (sessionId) {
          handlers.onSessionIdle?.(sessionId);
        }
        break;
      }
      case "session.error": {
        const sessionId = openCodeSessionId(properties);
        if (sessionId) {
          const error =
            typeof properties.error === "string"
              ? properties.error
              : extractOpenCodeError(properties.error);
          handlers.onSessionError?.(sessionId, error);
        }
        break;
      }
      case "session.compacted": {
        const sessionId = openCodeSessionId(properties);
        if (sessionId) {
          handlers.onSessionCompacted?.(sessionId);
        }
        break;
      }
      case "session.status": {
        const sessionId = openCodeSessionId(properties);
        const status = openCodeStatusName(properties.status);
        if (sessionId && status) {
          handlers.onSessionStatus?.(sessionId, status);
        }
        break;
      }
      case "message.updated": {
        this.handleMessageUpdated(properties, handlers);
        break;
      }
      case "message.part.updated": {
        this.handleMessagePartUpdated(properties, handlers);
        break;
      }
      default:
        break;
    }
  }

  private handleMessageUpdated(
    properties: Record<string, unknown>,
    handlers: OpenCodeEventHandlers,
  ): void {
    const message = extractOpenCodeMessage(properties);
    if (!message) {
      return;
    }
    if (message.role === "user" && typeof message.id === "string") {
      this.pendingUserMessages.set(message.id, message.sessionID ?? "");
      return;
    }
    handlers.onMessageUpdated?.(message);
  }

  private handleMessagePartUpdated(
    properties: Record<string, unknown>,
    handlers: OpenCodeEventHandlers,
  ): void {
    const sessionId =
      typeof properties.sessionID === "string" ? properties.sessionID : undefined;
    const messageID =
      typeof properties.messageID === "string" ? properties.messageID : undefined;
    const part = isObject(properties.part)
      ? (properties.part as unknown as OpenCodeMessagePart)
      : undefined;
    if (part && part.type === "text" && typeof part.text === "string" && part.messageID) {
      const pendingSessionId = this.pendingUserMessages.get(part.messageID);
      if (pendingSessionId) {
        this.pendingUserMessages.delete(part.messageID);
        handlers.onMessageUpdated?.({
          id: part.messageID,
          sessionID: pendingSessionId,
          role: "user",
          parts: [{ type: "text", text: part.text }],
        });
      }
    }
    handlers.onMessagePartUpdated?.({
      sessionID: sessionId ?? part?.sessionID,
      messageID: messageID ?? part?.messageID,
      part,
    });
  }
}

export function isEventStreamUnsupported(): boolean {
  return typeof fetch !== "function" || typeof ReadableStream !== "function";
}
