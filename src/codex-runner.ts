import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";

import type { SessionRecord } from "./domain.js";

export interface CodexExitResult {
  code: number | null;
  signal: NodeJS.Signals | null;
  stderr: string;
}

export class CodexRunner {
  private readonly running = new Map<string, ChildProcessWithoutNullStreams>();
  private readonly starting = new Set<string>();

  constructor(private readonly command: string) {}

  isRunning(sessionId: string): boolean {
    return this.starting.has(sessionId) || this.running.has(sessionId);
  }

  async resume(
    session: SessionRecord,
    prompt: string,
    onExit: (result: CodexExitResult) => void | Promise<void>,
  ): Promise<void> {
    if (!/^[a-zA-Z0-9_-]{8,128}$/.test(session.sessionId)) {
      throw new Error("会话 ID 格式异常，已拒绝启动远程继续。");
    }

    if (this.isRunning(session.sessionId)) {
      throw new Error("这个会话已经在通过飞书继续运行，请等待本轮结束。");
    }
    this.starting.add(session.sessionId);

    try {
      const child = spawn(
        this.command,
        [
          "exec",
          "resume",
          "--json",
          "--skip-git-repo-check",
          session.sessionId,
          "-",
        ],
        {
          cwd: session.cwd,
          env: process.env,
          shell: process.platform === "win32",
          stdio: ["pipe", "pipe", "pipe"],
          windowsHide: true,
        },
      );

      let stderr = "";
      let spawned = false;
      child.stdout.on("data", () => {
        // Drain JSONL output. Completion is delivered by the Stop hook instead.
      });
      child.stderr.on("data", (chunk: Buffer | string) => {
        stderr = `${stderr}${chunk.toString()}`;
      });
      child.once("close", (code, signal) => {
        this.starting.delete(session.sessionId);
        this.running.delete(session.sessionId);
        if (spawned) {
          void Promise.resolve(onExit({ code, signal, stderr })).catch((error) => {
            console.error("[resume] Exit handler failed:", error);
          });
        }
      });
      child.once("error", (error) => {
        stderr = `${stderr}\n${error.message}`;
      });

      await new Promise<void>((resolve, reject) => {
        const handleError = (error: Error): void => {
          child.off("spawn", handleSpawn);
          reject(error);
        };
        const handleSpawn = (): void => {
          child.off("error", handleError);
          spawned = true;
          this.running.set(session.sessionId, child);
          this.starting.delete(session.sessionId);
          resolve();
        };
        child.once("error", handleError);
        child.once("spawn", handleSpawn);
      });

      child.stdin.end(prompt, "utf8");
    } catch (error) {
      this.starting.delete(session.sessionId);
      this.running.delete(session.sessionId);
      throw error;
    }
  }
}
