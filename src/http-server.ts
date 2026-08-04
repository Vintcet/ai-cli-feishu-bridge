import { timingSafeEqual } from "node:crypto";
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
  health: (includeLocalSecrets: boolean) => Record<string, unknown>;
  shutdown: () => void;
  managedTerminalRegister: (payload: Record<string, unknown>) => Record<string, unknown>;
  managedTerminalUnregister: (
    payload: Record<string, unknown>,
  ) => Promise<Record<string, unknown>>;
  sessionAlias: (payload: Record<string, unknown>) => Promise<Record<string, unknown>>;
  sessionGroupRetry: (
    payload: Record<string, unknown>,
  ) => Promise<Record<string, unknown>>;
  sessionHistoryHide: (
    payload: Record<string, unknown>,
  ) => Promise<Record<string, unknown>>;
  runtimeLaunchClaim: () => Record<string, unknown>;
  runtimeLaunchComplete: (
    payload: Record<string, unknown>,
  ) => Promise<Record<string, unknown>>;
  localApproval: (payload: Record<string, unknown>) => Promise<Record<string, unknown>>;
  settingsUpdate: (payload: Record<string, unknown>) => Promise<Record<string, unknown>>;
  permission: (payload: PermissionHookPayload) => Promise<Record<string, unknown>>;
  requestUserInput: (
    payload: RequestUserInputHookPayload,
  ) => Promise<Record<string, unknown>>;
  activity: (payload: ActivityHookPayload) => Promise<Record<string, unknown>>;
  sessionStart: (payload: SessionStartHookPayload) => Promise<Record<string, unknown>>;
  sessionEnd: (payload: SessionEndHookPayload) => Promise<Record<string, unknown>>;
  stop: (payload: StopHookPayload) => Promise<Record<string, unknown>>;
  opencodeLaunch: (payload: Record<string, unknown>) => Promise<Record<string, unknown>>;
  opencodeRegister: (
    payload: Record<string, unknown>,
  ) => Promise<Record<string, unknown>>;
  opencodeUnregister: (
    payload: Record<string, unknown>,
  ) => Promise<Record<string, unknown>>;
}

export function startHookHttpServer(
  host: string,
  port: number,
  handlers: HookHttpHandlers,
  controlToken: string,
): http.Server {
  const server = http.createServer(async (request, response) => {
    try {
      await routeRequest(request, response, handlers, controlToken);
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
  controlToken: string,
): Promise<void> {
  const method = request.method ?? "GET";
  const url = new URL(request.url ?? "/", "http://127.0.0.1");

  if (method === "GET" && url.pathname === "/health") {
    // Unauthenticated callers get the same view minus the pairing code, which
    // would otherwise let any local process claim the Feishu owner binding.
    sendJson(response, 200, handlers.health(hasValidControlToken(request, controlToken)));
    return;
  }

  if (method !== "POST") {
    sendJson(response, 404, { error: "not_found" });
    return;
  }

  // Requiring a JSON content type keeps write requests outside the CORS
  // safelist, while Fetch Metadata rejects browser-originated variants before
  // any body is parsed. The control token remains the authorization boundary
  // for every state-changing endpoint, including hook callbacks.
  if (!hasJsonContentType(request)) {
    sendJson(response, 415, { ok: false, error: "请求必须使用 application/json。" });
    return;
  }
  if (isCrossSiteRequest(request)) {
    sendJson(response, 403, { ok: false, error: "拒绝跨站请求。" });
    return;
  }
  if (!hasValidControlToken(request, controlToken)) {
    sendJson(response, 401, { ok: false, error: "本机控制令牌无效。" });
    return;
  }

  if (url.pathname === "/control/shutdown") {
    response.once("finish", () => setImmediate(handlers.shutdown));
    sendJson(response, 202, { ok: true });
    return;
  }

  if (url.pathname === "/managed-terminals/register") {
    const body = await readJsonBody(request);
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
    const body = await readJsonBody(request);
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
    const body = await readJsonBody(request);
    if (!body || typeof body !== "object" || Array.isArray(body)) {
      sendJson(response, 400, { ok: false, error: "请求格式不正确。" });
      return;
    }
    const result = await handlers.sessionAlias(body as Record<string, unknown>);
    sendJson(response, result.ok === true ? 200 : 400, result);
    return;
  }

  if (url.pathname === "/sessions/feishu-group/retry") {
    if (!hasValidControlToken(request, controlToken)) {
      sendJson(response, 401, { ok: false, error: "本机控制身份验证失败。" });
      return;
    }
    const body = await readJsonBody(request);
    if (!body || typeof body !== "object" || Array.isArray(body)) {
      sendJson(response, 400, { ok: false, error: "请求格式不正确。" });
      return;
    }
    const result = await handlers.sessionGroupRetry(body as Record<string, unknown>);
    sendJson(response, result.ok === true ? 200 : 400, result);
    return;
  }

  if (url.pathname === "/sessions/history/hide") {
    if (!hasValidControlToken(request, controlToken)) {
      sendJson(response, 401, { ok: false, error: "本机控制身份验证失败。" });
      return;
    }
    const body = await readJsonBody(request);
    if (!body || typeof body !== "object" || Array.isArray(body)) {
      sendJson(response, 400, { ok: false, error: "请求格式不正确。" });
      return;
    }
    const result = await handlers.sessionHistoryHide(body as Record<string, unknown>);
    sendJson(response, result.ok === true ? 200 : 400, result);
    return;
  }

  if (url.pathname === "/runtime-launches/claim") {
    if (!hasValidControlToken(request, controlToken)) {
      sendJson(response, 401, { ok: false, error: "本机控制身份验证失败。" });
      return;
    }
    const body = await readJsonBody(request);
    if (!body || typeof body !== "object" || Array.isArray(body)) {
      sendJson(response, 400, { ok: false, error: "请求格式不正确。" });
      return;
    }
    sendJson(response, 200, handlers.runtimeLaunchClaim());
    return;
  }

  if (url.pathname === "/runtime-launches/complete") {
    if (!hasValidControlToken(request, controlToken)) {
      sendJson(response, 401, { ok: false, error: "本机控制身份验证失败。" });
      return;
    }
    const body = await readJsonBody(request);
    if (!body || typeof body !== "object" || Array.isArray(body)) {
      sendJson(response, 400, { ok: false, error: "请求格式不正确。" });
      return;
    }
    const result = await handlers.runtimeLaunchComplete(
      body as Record<string, unknown>,
    );
    sendJson(response, result.ok === true ? 200 : 400, result);
    return;
  }

  if (url.pathname === "/approvals/resolve") {
    if (!hasValidControlToken(request, controlToken)) {
      sendJson(response, 401, { ok: false, error: "本机控制身份验证失败。" });
      return;
    }
    const body = await readJsonBody(request);
    if (!body || typeof body !== "object" || Array.isArray(body)) {
      sendJson(response, 400, { ok: false, error: "请求格式不正确。" });
      return;
    }
    const result = await handlers.localApproval(body as Record<string, unknown>);
    sendJson(response, result.ok === true ? 200 : 400, result);
    return;
  }

  if (url.pathname === "/settings") {
    if (!hasValidControlToken(request, controlToken)) {
      sendJson(response, 401, { ok: false, error: "本机控制身份验证失败。" });
      return;
    }
    const body = await readJsonBody(request);
    if (!body || typeof body !== "object" || Array.isArray(body)) {
      sendJson(response, 400, { ok: false, error: "请求格式不正确。" });
      return;
    }
    const result = await handlers.settingsUpdate(body as Record<string, unknown>);
    sendJson(response, result.ok === true ? 200 : 400, result);
    return;
  }

  if (url.pathname === "/opencode/launch") {
    if (!hasValidControlToken(request, controlToken)) {
      sendJson(response, 401, { ok: false, error: "本机控制身份验证失败。" });
      return;
    }
    const body = await readJsonBody(request);
    if (!body || typeof body !== "object" || Array.isArray(body)) {
      sendJson(response, 400, { ok: false, error: "请求格式不正确。" });
      return;
    }
    const result = await handlers.opencodeLaunch(body as Record<string, unknown>);
    sendJson(response, result.ok === true ? 200 : 400, result);
    return;
  }

  if (url.pathname === "/opencode/register") {
    if (!hasValidControlToken(request, controlToken)) {
      sendJson(response, 401, { ok: false, error: "本机控制身份验证失败。" });
      return;
    }
    const body = await readJsonBody(request);
    if (!body || typeof body !== "object" || Array.isArray(body)) {
      sendJson(response, 400, { ok: false, error: "请求格式不正确。" });
      return;
    }
    const result = await handlers.opencodeRegister(body as Record<string, unknown>);
    sendJson(response, result.ok === true ? 200 : 400, result);
    return;
  }

  if (url.pathname === "/opencode/unregister") {
    if (!hasValidControlToken(request, controlToken)) {
      sendJson(response, 401, { ok: false, error: "本机控制身份验证失败。" });
      return;
    }
    const body = await readJsonBody(request);
    if (!body || typeof body !== "object" || Array.isArray(body)) {
      sendJson(response, 400, { ok: false, error: "请求格式不正确。" });
      return;
    }
    const result = await handlers.opencodeUnregister(body as Record<string, unknown>);
    sendJson(response, result.ok === true ? 200 : 400, result);
    return;
  }

  if (url.pathname === "/hooks/session-start") {
    const body = await readJsonBody(request);
    if (!isSessionStartHookPayload(body)) {
      sendJson(response, 400, {});
      return;
    }
    sendJson(response, 200, await handlers.sessionStart(body));
    return;
  }

  if (url.pathname === "/hooks/session-end") {
    const body = await readJsonBody(request);
    if (!isSessionEndHookPayload(body)) {
      sendJson(response, 400, {});
      return;
    }
    sendJson(response, 200, await handlers.sessionEnd(body));
    return;
  }

  if (url.pathname === "/hooks/permission") {
    const body = await readJsonBody(request);
    if (!isPermissionHookPayload(body)) {
      sendJson(response, 400, {});
      return;
    }
    sendJson(response, 200, await handlers.permission(body));
    return;
  }

  if (url.pathname === "/hooks/request-user-input") {
    const body = await readJsonBody(request);
    if (!isRequestUserInputHookPayload(body)) {
      sendJson(response, 400, {});
      return;
    }
    sendJson(response, 200, await handlers.requestUserInput(body));
    return;
  }

  if (url.pathname === "/hooks/activity") {
    const body = await readJsonBody(request);
    if (!isActivityHookPayload(body)) {
      sendJson(response, 400, {});
      return;
    }
    sendJson(response, 200, await handlers.activity(body));
    return;
  }

  if (url.pathname === "/hooks/stop") {
    const body = await readJsonBody(request);
    if (!isStopHookPayload(body)) {
      sendJson(response, 400, {});
      return;
    }
    sendJson(response, 200, await handlers.stop(body));
    return;
  }

  sendJson(response, 404, { error: "not_found" });
}

function hasJsonContentType(request: IncomingMessage): boolean {
  const header = request.headers["content-type"];
  const value = (Array.isArray(header) ? header[0] : header) ?? "";
  // Ignore charset and boundary parameters; only the media type matters here.
  return value.split(";")[0]!.trim().toLowerCase() === "application/json";
}

/**
 * Browsers attach Sec-Fetch-Site to every fetch/XHR. Local tooling (hook
 * scripts, the desktop panel, curl) does not send it at all, so an absent
 * header is treated as trusted while any cross-origin value is rejected.
 */
function isCrossSiteRequest(request: IncomingMessage): boolean {
  const header = request.headers["sec-fetch-site"];
  const value = (Array.isArray(header) ? header[0] : header) ?? "";
  return value !== "" && value !== "same-origin" && value !== "none";
}

function hasValidControlToken(
  request: IncomingMessage,
  expectedToken: string,
): boolean {
  const header = request.headers["x-codex-feishu-control-token"];
  const token = Array.isArray(header) ? header[0] : header;
  if (typeof token !== "string") {
    return false;
  }
  const actual = Buffer.from(token, "utf8");
  const expected = Buffer.from(expectedToken, "utf8");
  return actual.length === expected.length && timingSafeEqual(actual, expected);
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
