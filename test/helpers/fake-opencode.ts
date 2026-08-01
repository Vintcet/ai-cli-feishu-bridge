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
  sessions: Array<Record<string, unknown>> = [
    {
      id: "session-alpha",
      title: "demo",
      directory: "C:/demo",
      model: "openai/gpt-5",
    },
  ];
  permissionReplyResponses: Record<string, string> = {};
  healthOk = true;
  currentDirectory = "C:/demo";
  resetNextRequests = 0;

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
      response.on("close", () => this.sseClients.delete(response));
      return;
    }
    if (method === "GET" && url.pathname === "/session") {
      this.sendJson(response, 200, this.sessions);
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
