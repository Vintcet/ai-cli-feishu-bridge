import net from "node:net";
import path from "node:path";

import type { SessionRecord } from "./domain.js";

interface TerminalReply {
  ok?: boolean;
  error?: string;
}

export type TerminalSubmitMode = "steer" | "queue";

export interface ManagedTerminalRegistration {
  terminalId: string;
  cwd: string;
  normalizedCwd: string;
  elevated: boolean;
  ready: boolean;
  createdAt: number;
  lastSeenAt: number;
  sessionId?: string;
}

export interface ManagedTerminalClaim {
  terminalId: string;
  elevated: boolean;
  createdAt: number;
}

export function managedTerminalSessionId(terminalId: string): string {
  return `managed-terminal-${terminalId}`;
}

export class ManagedTerminalRouter {
  private readonly registrations = new Map<string, ManagedTerminalRegistration>();
  private readonly sendChains = new Map<string, Promise<void>>();

  isManaged(session: SessionRecord): boolean {
    return Boolean(session.managedTerminalId);
  }

  isOnline(session: SessionRecord): boolean {
    const terminalId = session.managedTerminalId;
    if (!terminalId) return false;
    const registration = this.registrations.get(terminalId);
    return Boolean(registration && Date.now() - registration.lastSeenAt <= 20_000);
  }

  isReady(session: SessionRecord): boolean {
    const terminalId = session.managedTerminalId;
    if (!terminalId) return false;
    const registration = this.registrations.get(terminalId);
    return Boolean(
      registration &&
        registration.ready &&
        Date.now() - registration.lastSeenAt <= 20_000 &&
        (!registration.sessionId || registration.sessionId === session.sessionId),
    );
  }

  listOnline(now = Date.now()): ManagedTerminalRegistration[] {
    this.prune(now);
    return [...this.registrations.values()]
      .filter((registration) => now - registration.lastSeenAt <= 20_000)
      .sort((left, right) => right.createdAt - left.createdAt);
  }

  register(
    value: {
      terminalId?: unknown;
      cwd?: unknown;
      elevated?: unknown;
      ready?: unknown;
    },
    existingSessionId?: string,
  ): void {
    const terminalId = typeof value.terminalId === "string" ? value.terminalId : "";
    const cwd = typeof value.cwd === "string" ? value.cwd : "";
    if (!/^[a-zA-Z0-9_-]{8,64}$/.test(terminalId) || !path.isAbsolute(cwd)) {
      throw new Error("托管终端注册信息无效。");
    }
    const now = Date.now();
    const current = this.registrations.get(terminalId);
    this.registrations.set(terminalId, {
      terminalId,
      cwd: path.resolve(cwd),
      normalizedCwd: normalizeCwd(cwd),
      elevated: value.elevated === true,
      ready:
        typeof value.ready === "boolean"
          ? value.ready
          : current?.ready ?? true,
      createdAt: current?.createdAt ?? now,
      lastSeenAt: now,
      sessionId: existingSessionId ?? current?.sessionId,
    });
    this.prune(now);
  }

  unregister(value: { terminalId?: unknown }): void {
    const terminalId = typeof value.terminalId === "string" ? value.terminalId : "";
    if (!/^[a-zA-Z0-9_-]{8,64}$/.test(terminalId)) {
      throw new Error("托管终端注销信息无效。");
    }
    this.registrations.delete(terminalId);
  }

  claim(cwd: string, sessionId: string): ManagedTerminalClaim | undefined {
    const now = Date.now();
    this.prune(now);
    const normalizedCwd = normalizeCwd(cwd);
    const candidate = [...this.registrations.values()]
      .filter(
        (registration) =>
          registration.normalizedCwd === normalizedCwd &&
          (!registration.sessionId || registration.sessionId === sessionId),
      )
      .sort((left, right) => left.createdAt - right.createdAt)[0];
    if (!candidate) return undefined;
    candidate.sessionId = sessionId;
    candidate.ready = true;
    return {
      terminalId: candidate.terminalId,
      elevated: candidate.elevated,
      createdAt: candidate.createdAt,
    };
  }

  claimById(
    terminalId: string,
    cwd: string,
    sessionId: string,
  ): ManagedTerminalClaim | undefined {
    if (!/^[a-zA-Z0-9_-]{8,64}$/.test(terminalId) || !path.isAbsolute(cwd)) {
      throw new Error("托管终端认领信息无效。");
    }
    const now = Date.now();
    this.prune(now);
    const registration = this.registrations.get(terminalId);
    if (!registration) {
      return undefined;
    }
    if (registration.normalizedCwd !== normalizeCwd(cwd)) {
      throw new Error("托管终端 ID 与项目目录不匹配，已拒绝认领。");
    }
    if (registration.sessionId && registration.sessionId !== sessionId) {
      throw new Error("托管终端已经属于另一个 Codex 会话。");
    }
    registration.sessionId = sessionId;
    registration.ready = true;
    return {
      terminalId: registration.terminalId,
      elevated: registration.elevated,
      createdAt: registration.createdAt,
    };
  }

  release(sessionId: string): void {
    for (const registration of this.registrations.values()) {
      if (registration.sessionId === sessionId) {
        registration.sessionId = undefined;
      }
    }
  }

  async send(
    session: SessionRecord,
    prompt: string,
    submitMode: TerminalSubmitMode = "steer",
  ): Promise<void> {
    const terminalId = session.managedTerminalId;
    if (!terminalId || !/^[a-zA-Z0-9_-]{8,64}$/.test(terminalId)) {
      throw new Error("托管终端 ID 无效，请从桌面助手重新打开这个 Codex 窗口。");
    }

    const normalizedPrompt = prompt.replace(/[\r\n]+/g, " ").trim();
    if (!normalizedPrompt) {
      throw new Error("回复内容不能为空。");
    }
    if (normalizedPrompt.length > 8_000) {
      throw new Error("回复内容过长，请控制在 8000 字以内。");
    }
    if (submitMode !== "steer" && submitMode !== "queue") {
      throw new Error("托管终端提交模式无效。");
    }

    const registration = this.registrations.get(terminalId);
    if (!registration || Date.now() - registration.lastSeenAt > 20_000) {
      throw new Error("对应的 Codex 窗口已经关闭或暂时离线。");
    }
    if (!registration.ready) {
      throw new Error("Codex 窗口仍在启动，请稍等几秒后再回复。");
    }
    if (registration.sessionId && registration.sessionId !== session.sessionId) {
      throw new Error("托管终端与目标会话不匹配，已拒绝输入以避免串线。");
    }

    const previous = this.sendChains.get(terminalId) ?? Promise.resolve();
    const current = previous.catch(() => undefined).then(
      () => this.sendNow(terminalId, normalizedPrompt, submitMode),
    );
    this.sendChains.set(terminalId, current);
    try {
      await current;
    } finally {
      if (this.sendChains.get(terminalId) === current) {
        this.sendChains.delete(terminalId);
      }
    }
  }

  private async sendNow(
    terminalId: string,
    prompt: string,
    submitMode: TerminalSubmitMode,
  ): Promise<void> {
    let lastError: Error | undefined;
    for (let attempt = 1; attempt <= 4; attempt += 1) {
      try {
        await this.sendOnce(terminalId, prompt, submitMode);
        return;
      } catch (error) {
        lastError = error instanceof Error ? error : new Error(String(error));
        if (!isRetryablePipeError(lastError) || attempt === 4) {
          throw lastError;
        }
        await new Promise((resolve) => setTimeout(resolve, attempt * 150));
      }
    }
    throw lastError ?? new Error("无法连接托管 Codex 窗口。");
  }

  private async sendOnce(
    terminalId: string,
    prompt: string,
    submitMode: TerminalSubmitMode,
  ): Promise<void> {
    const pipePath = `\\\\.\\pipe\\CodexFeishu.${terminalId}`;
    await new Promise<void>((resolve, reject) => {
      const socket = net.createConnection(pipePath);
      let settled = false;
      let responseText = "";

      const finish = (error?: Error): void => {
        if (settled) return;
        settled = true;
        socket.destroy();
        if (error) reject(error);
        else resolve();
      };

      socket.setEncoding("utf8");
      socket.setTimeout(7_000);
      socket.once("connect", () => {
        socket.write(`${JSON.stringify({ type: "prompt", prompt, submitMode })}\n`);
      });
      socket.on("data", (chunk) => {
        responseText += chunk;
        const newlineIndex = responseText.indexOf("\n");
        if (newlineIndex < 0) return;
        try {
          const reply = JSON.parse(responseText.slice(0, newlineIndex)) as TerminalReply;
          if (reply.ok) {
            finish();
          } else {
            finish(new Error(reply.error || "托管终端没有接受这条回复。"));
          }
        } catch {
          finish(new Error("托管终端返回了无法识别的结果。"));
        }
      });
      socket.once("timeout", () =>
        finish(pipeError("ETIMEDOUT", "连接托管 Codex 窗口超时。")),
      );
      socket.once("error", (error: NodeJS.ErrnoException) => {
        if (error.code === "EACCES") {
          finish(pipeError(
            error.code,
            "管理员 Codex 窗口拒绝了桥接连接。请用最新版桌面助手重新打开这个会话。",
          ));
          return;
        }
        finish(pipeError(
          error.code ?? "EPIPE",
          "对应的 Codex 窗口暂时无法接收输入。",
        ));
      });
      socket.once("close", () => {
        if (!settled) {
          finish(pipeError("ECONNRESET", "对应的 Codex 窗口在接收回复前已关闭。"));
        }
      });
    });
  }

  private prune(now: number): void {
    for (const [terminalId, registration] of this.registrations) {
      if (now - registration.lastSeenAt > 60_000) {
        this.registrations.delete(terminalId);
      }
    }
  }
}

function pipeError(code: string, message: string): Error {
  const error = new Error(message) as NodeJS.ErrnoException;
  error.code = code;
  return error;
}

function isRetryablePipeError(error: Error): boolean {
  const code = (error as NodeJS.ErrnoException).code;
  return code === "ENOENT" ||
    code === "ECONNREFUSED" ||
    code === "ECONNRESET" ||
    code === "EBUSY" ||
    code === "EPIPE" ||
    code === "ETIMEDOUT";
}

function normalizeCwd(cwd: string): string {
  const resolved = path.resolve(cwd);
  return process.platform === "win32" ? resolved.toLowerCase() : resolved;
}
