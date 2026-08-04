import {
  asOpenCodeMessage,
  asOpenCodePermission,
  asOpenCodePermissionReplied,
  asOpenCodeQuestionRejected,
  asOpenCodeQuestionReplied,
  asOpenCodeQuestionRequest,
  asOpenCodeSession,
  extractOpenCodeError,
  extractOpenCodeMessage,
  extractOpenCodeSession,
  isObject,
  openCodePermissionList,
  openCodeSessionId,
  openCodeStatusName,
  type OpenCodeEventHandlers,
  type OpenCodeMessage,
  type OpenCodeMessagePart,
  type OpenCodeModel,
  type OpenCodePermission,
  type OpenCodePermissionResponse,
  type OpenCodeQuestionRequest,
  type OpenCodeSession,
} from "./opencode-protocol.js";

export {
  asOpenCodeMessage,
  asOpenCodePermission,
  asOpenCodeQuestionRequest,
  asOpenCodeSession,
  type OpenCodeEvent,
  type OpenCodeEventHandlers,
  type OpenCodeMessage,
  type OpenCodeMessagePart,
  type OpenCodeMessagePartUpdatedProperties,
  type OpenCodeModel,
  type OpenCodePermission,
  type OpenCodePermissionReplied,
  type OpenCodePermissionResponse,
  type OpenCodeQuestion,
  type OpenCodeQuestionOption,
  type OpenCodeQuestionRejected,
  type OpenCodeQuestionReplied,
  type OpenCodeQuestionRequest,
  type OpenCodeSession,
} from "./opencode-protocol.js";

function responseErrorMessage(
  response: Response,
  context: string,
): string {
  return `${context}: HTTP ${response.status} ${response.statusText}`;
}

export class OpenCodeClient {
  private readonly pendingUserMessages = new Map<
    string,
    { sessionId: string; createdAt: number }
  >();
  private readonly pendingUserMessageTtlMs: number;
  private readonly maxPendingUserMessages: number;
  private pendingUserMessageCleanupTimer:
    | ReturnType<typeof setTimeout>
    | undefined;

  constructor(
    private readonly baseUrl: string,
    private readonly defaultDirectory?: string,
    options: {
      pendingUserMessageTtlMs?: number;
      maxPendingUserMessages?: number;
    } = {},
  ) {
    this.pendingUserMessageTtlMs = Math.max(
      1,
      Math.trunc(options.pendingUserMessageTtlMs ?? 5 * 60_000),
    );
    this.maxPendingUserMessages = Math.max(
      1,
      Math.trunc(options.maxPendingUserMessages ?? 1_000),
    );
  }

  private url(path: string): string {
    return `${this.baseUrl}${path}`;
  }

  private scopedPath(path: string, directory = this.defaultDirectory): string {
    if (!directory) {
      return path;
    }
    const separator = path.includes("?") ? "&" : "?";
    return `${path}${separator}directory=${encodeURIComponent(directory)}`;
  }

  private v2LocationPath(path: string): string {
    if (!this.defaultDirectory) {
      return path;
    }
    const separator = path.includes("?") ? "&" : "?";
    return `${path}${separator}location%5Bdirectory%5D=${encodeURIComponent(
      this.defaultDirectory,
    )}`;
  }

  private async getJson(
    path: string,
    timeoutMs: number,
  ): Promise<{ response: Response; body: unknown }> {
    let lastError: unknown;
    for (let attempt = 0; attempt < 2; attempt += 1) {
      try {
        const response = await fetch(this.url(path), {
          signal: AbortSignal.timeout(timeoutMs),
        });
        if (!response.ok) {
          return { response, body: undefined };
        }
        return { response, body: await response.json() as unknown };
      } catch (error) {
        lastError = error;
        if (attempt === 0) {
          await new Promise((resolve) => setTimeout(resolve, 25));
        }
      }
    }
    throw lastError instanceof Error ? lastError : new Error(String(lastError));
  }

  async health(): Promise<boolean> {
    try {
      const { response, body } = await this.getJson("/global/health", 3000);
      if (!response.ok) {
        return false;
      }
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
      const { response, body } = await this.getJson("/global/health", 2000);
      if (!response.ok) {
        return { healthy: false };
      }
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
      const { response, body } = await this.getJson("/path", 3000);
      if (!response.ok) {
        return undefined;
      }
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
    const { response, body } = await this.getJson(this.scopedPath("/session"), 10000);
    if (!response.ok) {
      throw new Error(responseErrorMessage(response, "列出会话"));
    }
    if (!Array.isArray(body)) {
      return [];
    }
    return body
      .map((item) => asOpenCodeSession(item))
      .filter((item): item is OpenCodeSession => Boolean(item));
  }

  async listActiveSessionIds(): Promise<string[]> {
    const { response, body } = await this.getJson("/api/session/active", 5000);
    if (response.status === 404 || response.status === 405) {
      return [];
    }
    if (!response.ok) {
      throw new Error(responseErrorMessage(response, "读取活动会话"));
    }
    if (!isObject(body) || !isObject(body.data)) {
      return [];
    }
    return Object.entries(body.data)
      .filter(
        ([sessionId, status]) =>
          sessionId.length > 0 &&
          isObject(status) &&
          status.type === "running",
      )
      .map(([sessionId]) => sessionId);
  }

  async getSession(sessionId: string): Promise<OpenCodeSession | undefined> {
    try {
      const { response, body } = await this.getJson(
        this.scopedPath(`/session/${encodeURIComponent(sessionId)}`),
        10000,
      );
      if (!response.ok) {
        return undefined;
      }
      return asOpenCodeSession(body);
    } catch {
      return undefined;
    }
  }

  async createSession(title?: string): Promise<OpenCodeSession> {
    const response = await fetch(this.url(this.scopedPath("/session")), {
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
      this.url(this.scopedPath(`/session/${encodeURIComponent(sessionId)}/prompt_async`)),
      {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({
          model: promptModel(options.model),
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
    const v2 = await fetch(
      this.url(
        `/api/session/${encodeURIComponent(sessionId)}/permission/${encodeURIComponent(permissionId)}/reply`,
      ),
      {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ reply: response }),
        signal: AbortSignal.timeout(10000),
      },
    );
    if (v2.ok) {
      return;
    }
    if (v2.status !== 404 && v2.status !== 405) {
      throw new Error(responseErrorMessage(v2, "回复权限"));
    }
    const modern = await fetch(
      this.url(this.scopedPath(`/permission/${encodeURIComponent(permissionId)}/reply`)),
      {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ reply: response }),
        signal: AbortSignal.timeout(10000),
      },
    );
    if (modern.ok) {
      return;
    }
    if (modern.status !== 404 && modern.status !== 405) {
      throw new Error(responseErrorMessage(modern, "回复权限"));
    }
    const legacy = await fetch(
      this.url(this.scopedPath(
        `/session/${encodeURIComponent(sessionId)}/permissions/${encodeURIComponent(permissionId)}`,
      )),
      {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ response }),
        signal: AbortSignal.timeout(10000),
      },
    );
    if (!legacy.ok) {
      throw new Error(responseErrorMessage(legacy, "回复权限"));
    }
  }

  async listPermissions(): Promise<OpenCodePermission[]> {
    const v2 = await this.getJson(this.v2LocationPath("/api/permission/request"), 10000);
    if (v2.response.ok) {
      return openCodePermissionList(v2.body);
    }
    if (v2.response.status !== 404 && v2.response.status !== 405) {
      throw new Error(responseErrorMessage(v2.response, "读取待处理权限"));
    }
    const legacy = await this.getJson(this.scopedPath("/permission"), 10000);
    if (legacy.response.status === 404 || legacy.response.status === 405) {
      return [];
    }
    if (!legacy.response.ok) {
      throw new Error(responseErrorMessage(legacy.response, "读取待处理权限"));
    }
    return openCodePermissionList(legacy.body);
  }

  async listQuestions(): Promise<OpenCodeQuestionRequest[]> {
    const { response, body } = await this.getJson(this.scopedPath("/question"), 10000);
    if (response.status === 404 || response.status === 405) {
      return [];
    }
    if (!response.ok) {
      throw new Error(responseErrorMessage(response, "读取待处理问题"));
    }
    return Array.isArray(body)
      ? body
          .map(asOpenCodeQuestionRequest)
          .filter((item): item is OpenCodeQuestionRequest => Boolean(item))
      : [];
  }

  async replyQuestion(requestId: string, answers: string[][]): Promise<void> {
    const response = await fetch(
      this.url(this.scopedPath(`/question/${encodeURIComponent(requestId)}/reply`)),
      {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ answers }),
        signal: AbortSignal.timeout(10000),
      },
    );
    if (!response.ok) {
      throw new Error(responseErrorMessage(response, "回复问题"));
    }
  }

  async rejectQuestion(requestId: string): Promise<void> {
    const response = await fetch(
      this.url(this.scopedPath(`/question/${encodeURIComponent(requestId)}/reject`)),
      {
        method: "POST",
        signal: AbortSignal.timeout(10000),
      },
    );
    if (!response.ok) {
      throw new Error(responseErrorMessage(response, "拒绝问题"));
    }
  }

  async abort(sessionId: string): Promise<void> {
    const response = await fetch(
      this.url(this.scopedPath(`/session/${encodeURIComponent(sessionId)}/abort`)),
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
      this.url(this.scopedPath(`/session/${encodeURIComponent(sessionId)}/undo`)),
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
    const { response, body } = await this.getJson(
      this.scopedPath(
        `/session/${encodeURIComponent(sessionId)}/message?limit=${Math.max(1, limit)}`,
      ),
      10000,
    );
    if (!response.ok) {
      throw new Error(responseErrorMessage(response, "读取消息"));
    }
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
      this.clearPendingUserMessages();
      try {
        const response = await fetch(this.url(this.scopedPath("/event")), {
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
        this.clearPendingUserMessages();
      }
    };
    void run();
    return {
      close: () => {
        controller.abort();
        this.clearPendingUserMessages();
      },
    };
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
        buffer = buffer.replace(/\r\n/gu, "\n");
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
      case "permission.asked":
      case "permission.v2.asked": {
        const permission = asOpenCodePermission(properties);
        if (permission) {
          handlers.onPermissionAsked?.(permission);
        }
        break;
      }
      case "permission.updated": {
        const permission = asOpenCodePermission(properties);
        if (permission) {
          handlers.onPermissionUpdated?.(permission);
        }
        break;
      }
      case "permission.replied":
      case "permission.v2.replied": {
        const permission = asOpenCodePermissionReplied(properties);
        if (permission) {
          handlers.onPermissionReplied?.(permission);
        }
        break;
      }
      case "question.asked": {
        const request = asOpenCodeQuestionRequest(properties);
        if (request) {
          handlers.onQuestionAsked?.(request);
        }
        break;
      }
      case "question.replied": {
        const reply = asOpenCodeQuestionReplied(properties);
        if (reply) {
          handlers.onQuestionReplied?.(reply);
        }
        break;
      }
      case "question.rejected": {
        const rejection = asOpenCodeQuestionRejected(properties);
        if (rejection) {
          handlers.onQuestionRejected?.(rejection);
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
      case "session.updated": {
        const session = extractOpenCodeSession(properties);
        if (session) {
          handlers.onSessionUpdated?.(session);
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
      if (
        (message.parts ?? []).some(
          (part) => part.type === "text" && typeof part.text === "string",
        )
      ) {
        handlers.onMessageUpdated?.(message);
        return;
      }
      this.rememberPendingUserMessage(message.id, message.sessionID ?? "");
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
      const pending = this.pendingUserMessages.get(part.messageID);
      if (pending) {
        this.deletePendingUserMessage(part.messageID);
        handlers.onMessageUpdated?.({
          id: part.messageID,
          sessionID: pending.sessionId || sessionId || part.sessionID,
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

  private rememberPendingUserMessage(messageId: string, sessionId: string): void {
    const now = Date.now();
    this.prunePendingUserMessages(now);
    this.pendingUserMessages.delete(messageId);
    while (this.pendingUserMessages.size >= this.maxPendingUserMessages) {
      const oldest = this.pendingUserMessages.keys().next().value as
        | string
        | undefined;
      if (!oldest) {
        break;
      }
      this.pendingUserMessages.delete(oldest);
    }
    this.pendingUserMessages.set(messageId, { sessionId, createdAt: now });
    this.schedulePendingUserMessageCleanup();
  }

  private deletePendingUserMessage(messageId: string): void {
    this.pendingUserMessages.delete(messageId);
    if (this.pendingUserMessages.size === 0) {
      this.clearPendingUserMessageCleanupTimer();
    }
  }

  private prunePendingUserMessages(now = Date.now()): void {
    for (const [messageId, pending] of this.pendingUserMessages) {
      if (now - pending.createdAt >= this.pendingUserMessageTtlMs) {
        this.pendingUserMessages.delete(messageId);
      }
    }
    if (this.pendingUserMessages.size === 0) {
      this.clearPendingUserMessageCleanupTimer();
    }
  }

  private schedulePendingUserMessageCleanup(): void {
    if (
      this.pendingUserMessageCleanupTimer ||
      this.pendingUserMessages.size === 0
    ) {
      return;
    }
    let nextExpiry = Number.POSITIVE_INFINITY;
    for (const pending of this.pendingUserMessages.values()) {
      nextExpiry = Math.min(
        nextExpiry,
        pending.createdAt + this.pendingUserMessageTtlMs,
      );
    }
    const delay = Math.max(1, nextExpiry - Date.now());
    this.pendingUserMessageCleanupTimer = setTimeout(() => {
      this.pendingUserMessageCleanupTimer = undefined;
      this.prunePendingUserMessages();
      this.schedulePendingUserMessageCleanup();
    }, delay);
    this.pendingUserMessageCleanupTimer.unref?.();
  }

  private clearPendingUserMessages(): void {
    this.pendingUserMessages.clear();
    this.clearPendingUserMessageCleanupTimer();
  }

  private clearPendingUserMessageCleanupTimer(): void {
    if (!this.pendingUserMessageCleanupTimer) {
      return;
    }
    clearTimeout(this.pendingUserMessageCleanupTimer);
    this.pendingUserMessageCleanupTimer = undefined;
  }
}

function promptModel(value: string | undefined): OpenCodeModel | undefined {
  if (!value) {
    return undefined;
  }
  const separator = value.indexOf("/");
  if (separator <= 0 || separator >= value.length - 1) {
    return undefined;
  }
  return {
    providerID: value.slice(0, separator),
    modelID: value.slice(separator + 1),
  };
}

export function isEventStreamUnsupported(): boolean {
  return typeof fetch !== "function" || typeof ReadableStream !== "function";
}
