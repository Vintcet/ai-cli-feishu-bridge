import http, { type IncomingMessage, type ServerResponse } from "node:http";

import {
  isActivityHookPayload,
  isPermissionHookPayload,
  isRequestUserInputHookPayload,
  isSessionEndHookPayload,
  isSessionStartHookPayload,
  isStopHookPayload,
  type ActivityHookPayload,
  type PermissionHookPayload,
  type RequestUserInputHookPayload,
  type SessionEndHookPayload,
  type SessionStartHookPayload,
  type StopHookPayload,
} from "./domain.js";

export interface HookHttpHandlers {
  health: () => Record<string, unknown>;
  managedTerminalRegister: (payload: Record<string, unknown>) => Record<string, unknown>;
  managedTerminalUnregister: (
    payload: Record<string, unknown>,
  ) => Promise<Record<string, unknown>>;
  sessionAlias: (payload: Record<string, unknown>) => Promise<Record<string, unknown>>;
  permission: (payload: PermissionHookPayload) => Promise<Record<string, unknown>>;
  requestUserInput: (
    payload: RequestUserInputHookPayload,
  ) => Promise<Record<string, unknown>>;
  activity: (payload: ActivityHookPayload) => Promise<Record<string, unknown>>;
  sessionStart: (payload: SessionStartHookPayload) => Promise<Record<string, unknown>>;
  sessionEnd: (payload: SessionEndHookPayload) => Promise<Record<string, unknown>>;
  stop: (payload: StopHookPayload) => Promise<Record<string, unknown>>;
}

export function startHookHttpServer(
  host: string,
  port: number,
  handlers: HookHttpHandlers,
): http.Server {
  const server = http.createServer(async (request, response) => {
    try {
      await routeRequest(request, response, handlers);
    } catch (error) {
      console.error("[http] Hook request failed:", error);
      if (!response.headersSent) {
        sendJson(response, 500, {});
      } else {
        response.end();
      }
    }
  });

  server.requestTimeout = 0;
  server.timeout = 0;
  server.keepAliveTimeout = 5_000;
  server.listen(port, host, () => {
    console.log(`[http] Codex hook bridge listening on http://${host}:${port}`);
  });
  return server;
}

async function routeRequest(
  request: IncomingMessage,
  response: ServerResponse,
  handlers: HookHttpHandlers,
): Promise<void> {
  const method = request.method ?? "GET";
  const url = new URL(request.url ?? "/", "http://127.0.0.1");

  if (method === "GET" && url.pathname === "/health") {
    sendJson(response, 200, handlers.health());
    return;
  }

  if (method !== "POST") {
    sendJson(response, 404, { error: "not_found" });
    return;
  }

  const body = await readJsonBody(request);
  if (url.pathname === "/managed-terminals/register") {
    if (!body || typeof body !== "object" || Array.isArray(body)) {
      sendJson(response, 400, {});
      return;
    }
    sendJson(
      response,
      200,
      handlers.managedTerminalRegister(body as Record<string, unknown>),
    );
    return;
  }

  if (url.pathname === "/managed-terminals/unregister") {
    if (!body || typeof body !== "object" || Array.isArray(body)) {
      sendJson(response, 400, {});
      return;
    }
    sendJson(
      response,
      200,
      await handlers.managedTerminalUnregister(body as Record<string, unknown>),
    );
    return;
  }

  if (url.pathname === "/sessions/alias") {
    if (!body || typeof body !== "object" || Array.isArray(body)) {
      sendJson(response, 400, { ok: false, error: "请求格式不正确。" });
      return;
    }
    const result = await handlers.sessionAlias(body as Record<string, unknown>);
    sendJson(response, result.ok === true ? 200 : 400, result);
    return;
  }

  if (url.pathname === "/hooks/session-start") {
    if (!isSessionStartHookPayload(body)) {
      sendJson(response, 400, {});
      return;
    }
    sendJson(response, 200, await handlers.sessionStart(body));
    return;
  }

  if (url.pathname === "/hooks/session-end") {
    if (!isSessionEndHookPayload(body)) {
      sendJson(response, 400, {});
      return;
    }
    sendJson(response, 200, await handlers.sessionEnd(body));
    return;
  }

  if (url.pathname === "/hooks/permission") {
    if (!isPermissionHookPayload(body)) {
      sendJson(response, 400, {});
      return;
    }
    sendJson(response, 200, await handlers.permission(body));
    return;
  }

  if (url.pathname === "/hooks/request-user-input") {
    if (!isRequestUserInputHookPayload(body)) {
      sendJson(response, 400, {});
      return;
    }
    sendJson(response, 200, await handlers.requestUserInput(body));
    return;
  }

  if (url.pathname === "/hooks/activity") {
    if (!isActivityHookPayload(body)) {
      sendJson(response, 400, {});
      return;
    }
    sendJson(response, 200, await handlers.activity(body));
    return;
  }

  if (url.pathname === "/hooks/stop") {
    if (!isStopHookPayload(body)) {
      sendJson(response, 400, {});
      return;
    }
    sendJson(response, 200, await handlers.stop(body));
    return;
  }

  sendJson(response, 404, { error: "not_found" });
}

async function readJsonBody(request: IncomingMessage): Promise<unknown> {
  const chunks: Buffer[] = [];
  let size = 0;
  for await (const chunk of request) {
    const buffer = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
    size += buffer.length;
    if (size > 1024 * 1024) {
      throw new Error("Hook request body exceeds 1 MiB.");
    }
    chunks.push(buffer);
  }
  const text = Buffer.concat(chunks).toString("utf8");
  return text ? JSON.parse(text) : {};
}

function sendJson(response: ServerResponse, status: number, value: unknown): void {
  const body = JSON.stringify(value);
  response.writeHead(status, {
    "content-type": "application/json; charset=utf-8",
    "content-length": Buffer.byteLength(body),
    "cache-control": "no-store",
  });
  response.end(body);
}
