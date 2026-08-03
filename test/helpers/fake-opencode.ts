import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";

export class FakeOpenCodeServer {
  readonly server = createServer((request, response) => {
    this.route(request, response).catch((error) => {
      console.error("[fake-opencode]", error);
      if (!response.headersSent) {
        response.writeHead(500).end("{}");
      }
    });
  });
  readonly requests: Array<{ method: string; url: string; body: unknown }> = [];
  private sseClients = new Set<ServerResponse>();
  private sseCounter = 0;
  sseConnectionCount = 0;
  sessions: Array<Record<string, unknown>> = [
    {
      id: "session-alpha",
      title: "demo",
      directory: "C:/demo",
      model: "openai/gpt-5",
    },
  ];
  activeSessionIds: string[] = ["session-alpha"];
  permissionReplyResponses: Record<string, string> = {};
  questionReplyAnswers: Record<string, string[][]> = {};
  questionRejections: string[] = [];
  permissions: Array<Record<string, unknown>> = [];
  questions: Array<Record<string, unknown>> = [];
  modernPermissionEndpoint = true;
  v2PermissionListStatus = 404;
  v2PermissionReplyStatus = 404;
  permissionListStatus = 200;
  questionListStatus = 200;
  healthOk = true;
  currentDirectory = "C:/demo";
  resetNextRequests = 0;

  get activeSseClients(): number {
    return this.sseClients.size;
  }

  async listen(): Promise<number> {
    await new Promise<void>((resolve, reject) => {
      this.server.once("listening", resolve);
      this.server.once("error", reject);
      this.server.listen(0, "127.0.0.1");
    });
    return (this.server.address() as AddressInfo).port;
  }

  async listenOn(port: number): Promise<void> {
    await new Promise<void>((resolve, reject) => {
      this.server.once("listening", resolve);
      this.server.once("error", reject);
      this.server.listen(port, "127.0.0.1");
    });
  }

  async close(): Promise<void> {
    for (const client of this.sseClients) {
      client.end();
    }
    this.sseClients.clear();
    await new Promise<void>((resolve, reject) => {
      this.server.close((error) => (error ? reject(error) : resolve()));
    });
  }

  sendSse(event: string, data: unknown): void {
    const payload = {
      id: `evt_test_${++this.sseCounter}`,
      type: event,
      properties: data,
    };
    this.sendSseRaw(`data: ${JSON.stringify(payload)}\n\n`);
  }

  sendSseRaw(frame: string): void {
    for (const client of this.sseClients) {
      client.write(frame);
    }
  }

  private async route(request: IncomingMessage, response: ServerResponse): Promise<void> {
    const url = new URL(request.url ?? "/", "http://127.0.0.1");
    const method = request.method ?? "GET";
    if (this.resetNextRequests > 0) {
      this.resetNextRequests -= 1;
      request.socket.destroy();
      return;
    }
    const body = await readBody(request);
    this.requests.push({ method, url: url.pathname + url.search, body });

    if (method === "GET" && url.pathname === "/global/health") {
      this.sendJson(response, 200, {
        healthy: this.healthOk,
        version: "1.18.10",
      });
      return;
    }
    if (method === "GET" && url.pathname === "/path") {
      this.sendJson(response, 200, {
        home: "C:/Users/demo",
        state: "C:/Users/demo/.local/state/opencode",
        config: "C:/Users/demo/.config/opencode",
        worktree: "/",
        directory: this.currentDirectory,
      });
      return;
    }
    if (method === "GET" && url.pathname === "/event") {
      response.writeHead(200, {
        "content-type": "text/event-stream",
        "cache-control": "no-store",
      });
      this.sseClients.add(response);
      this.sseConnectionCount += 1;
      response.on("close", () => this.sseClients.delete(response));
      return;
    }
    if (method === "GET" && url.pathname === "/session") {
      this.sendJson(response, 200, this.sessions);
      return;
    }
    if (method === "GET" && url.pathname === "/api/session/active") {
      this.sendJson(response, 200, {
        data: Object.fromEntries(
          this.activeSessionIds.map((sessionId) => [sessionId, { type: "running" }]),
        ),
      });
      return;
    }
    if (method === "GET" && url.pathname === "/api/permission/request") {
      this.sendJson(
        response,
        this.v2PermissionListStatus,
        this.v2PermissionListStatus === 200
          ? {
              location: { directory: this.currentDirectory },
              data: this.permissions,
            }
          : { error: "permission_v2_unavailable" },
      );
      return;
    }
    if (method === "GET" && url.pathname === "/permission") {
      this.sendJson(
        response,
        this.permissionListStatus,
        this.permissionListStatus === 200 ? this.permissions : { error: "permission_unavailable" },
      );
      return;
    }
    if (method === "GET" && url.pathname === "/question") {
      this.sendJson(
        response,
        this.questionListStatus,
        this.questionListStatus === 200 ? this.questions : { error: "question_unavailable" },
      );
      return;
    }
    const sessionMatch = url.pathname.match(/^\/session\/([^/]+)$/);
    if (method === "GET" && sessionMatch) {
      const session = this.sessions.find((item) => item.id === sessionMatch[1]);
      if (session) {
        this.sendJson(response, 200, session);
      } else {
        this.sendJson(response, 404, { error: "session_not_found" });
      }
      return;
    }
    if (method === "POST" && url.pathname === "/session") {
      const created = {
        id: `session-created-${this.requests.length}`,
        directory: "C:/demo",
        ...(typeof body === "object" && body && "title" in body
          ? { title: (body as { title?: string }).title }
          : {}),
      };
      this.sendJson(response, 200, created);
      return;
    }
    if (method === "POST" && /^\/session\/[^/]+\/prompt_async$/.test(url.pathname)) {
      response.writeHead(204).end();
      return;
    }
    const v2PermissionMatch = url.pathname.match(
      /^\/api\/session\/([^/]+)\/permission\/([^/]+)\/reply$/,
    );
    if (method === "POST" && v2PermissionMatch) {
      if (this.v2PermissionReplyStatus !== 200 && this.v2PermissionReplyStatus !== 204) {
        this.sendJson(response, this.v2PermissionReplyStatus, { error: "permission_v2_reply_failed" });
        return;
      }
      const permissionId = v2PermissionMatch[2];
      const reply =
        typeof body === "object" && body && "reply" in body
          ? String((body as { reply?: string }).reply)
          : "";
      this.permissionReplyResponses[permissionId] = reply;
      if (this.v2PermissionReplyStatus === 204) {
        response.writeHead(204).end();
      } else {
        this.sendJson(response, 200, true);
      }
      return;
    }
    const permissionMatch = url.pathname.match(/^\/session\/([^/]+)\/permissions\/([^/]+)$/);
    if (method === "POST" && permissionMatch) {
      const permissionId = permissionMatch[2];
      const reply =
        typeof body === "object" && body && "response" in body
          ? String((body as { response?: string }).response)
          : "";
      this.permissionReplyResponses[permissionId] = reply;
      this.sendJson(response, 200, true);
      return;
    }
    const modernPermissionMatch = url.pathname.match(/^\/permission\/([^/]+)\/reply$/);
    if (method === "POST" && modernPermissionMatch) {
      if (!this.modernPermissionEndpoint) {
        this.sendJson(response, 404, { error: "not_found" });
        return;
      }
      const permissionId = modernPermissionMatch[1];
      const reply =
        typeof body === "object" && body && "reply" in body
          ? String((body as { reply?: string }).reply)
          : "";
      this.permissionReplyResponses[permissionId] = reply;
      this.sendJson(response, 200, true);
      return;
    }
    const questionReplyMatch = url.pathname.match(/^\/question\/([^/]+)\/reply$/);
    if (method === "POST" && questionReplyMatch) {
      const answers =
        typeof body === "object" && body && "answers" in body &&
          Array.isArray((body as { answers?: unknown }).answers)
          ? (body as { answers: string[][] }).answers
          : [];
      this.questionReplyAnswers[questionReplyMatch[1]] = answers;
      this.questions = this.questions.filter((item) => item.id !== questionReplyMatch[1]);
      this.sendJson(response, 200, true);
      return;
    }
    const questionRejectMatch = url.pathname.match(/^\/question\/([^/]+)\/reject$/);
    if (method === "POST" && questionRejectMatch) {
      this.questionRejections.push(questionRejectMatch[1]);
      this.questions = this.questions.filter((item) => item.id !== questionRejectMatch[1]);
      this.sendJson(response, 200, true);
      return;
    }
    if (method === "POST" && /^\/session\/[^/]+\/(?:abort|undo)$/.test(url.pathname)) {
      this.sendJson(response, 200, true);
      return;
    }
    if (method === "GET" && /^\/session\/[^/]+\/message/.test(url.pathname)) {
      const sessionId = url.pathname.split("/")[2];
      this.sendJson(response, 200, [
        {
          info: {
            id: "msg-user-last",
            role: "user",
            sessionID: sessionId,
            time: { created: 1000 },
          },
          parts: [{ type: "text", text: "do the thing" }],
        },
        {
          info: {
            id: "msg-assistant-last",
            role: "assistant",
            sessionID: sessionId,
            time: { created: 1002, completed: 1003 },
          },
          parts: [
            { type: "text", text: "完成 ✅" },
            { type: "text", text: " 第二段" },
          ],
        },
      ]);
      return;
    }
    this.sendJson(response, 404, { error: "not_found" });
  }

  private sendJson(response: ServerResponse, status: number, value: unknown): void {
    const body = JSON.stringify(value);
    response.writeHead(status, {
      "content-type": "application/json",
      "content-length": Buffer.byteLength(body),
    });
    response.end(body);
  }
}

async function readBody(request: IncomingMessage): Promise<unknown> {
  const chunks: Buffer[] = [];
  for await (const chunk of request) {
    chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk));
  }
  const text = Buffer.concat(chunks).toString("utf8");
  return text ? JSON.parse(text) : {};
}
