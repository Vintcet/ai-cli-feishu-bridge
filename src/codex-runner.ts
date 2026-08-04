import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";

import type { SessionRecord } from "./domain.js";

const maxCapturedStderrChars = 64 * 1024;
const stderrTruncationMarker = "\n...[stderr output truncated]...\n";

export interface CodexExitResult {
  code: number | null;
  signal: NodeJS.Signals | null;
  stderr: string;
}

export class CodexRunner {
  private readonly running = new Map<string, ChildProcessWithoutNullStreams>();
  private readonly starting = new Set<string>();
  private readonly children = new Set<ChildProcessWithoutNullStreams>();

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
    let child: ChildProcessWithoutNullStreams | undefined;

    try {
      const spawnedChild = spawn(
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
      child = spawnedChild;
      this.children.add(spawnedChild);

      let stderr = "";
      let spawned = false;
      const appendStderr = (value: string): void => {
        stderr = `${stderr}${value}`;
        if (stderr.length > maxCapturedStderrChars) {
          stderr = `${stderrTruncationMarker}${stderr.slice(
            -(maxCapturedStderrChars - stderrTruncationMarker.length),
          )}`;
        }
      };
      const handleStdinError = (error: Error): void => {
        appendStderr(`\nstdin: ${error.message}`);
      };
      spawnedChild.stdout.on("data", () => {
        // Drain JSONL output. Completion is delivered by the Stop hook instead.
      });
      spawnedChild.stderr.on("data", (chunk: Buffer | string) => {
        appendStderr(chunk.toString());
      });
      spawnedChild.stdin.on("error", handleStdinError);
      spawnedChild.once("close", (code, signal) => {
        this.starting.delete(session.sessionId);
        this.running.delete(session.sessionId);
        this.children.delete(spawnedChild);
        spawnedChild.stdin.off("error", handleStdinError);
        if (spawned) {
          void Promise.resolve(onExit({ code, signal, stderr })).catch((error) => {
            console.error("[resume] Exit handler failed:", error);
          });
        }
      });
      spawnedChild.once("error", (error) => {
        appendStderr(`\n${error.message}`);
      });

      await new Promise<void>((resolve, reject) => {
        const handleError = (error: Error): void => {
          spawnedChild.off("spawn", handleSpawn);
          reject(error);
        };
        const handleSpawn = (): void => {
          spawnedChild.off("error", handleError);
          spawned = true;
          this.running.set(session.sessionId, spawnedChild);
          this.starting.delete(session.sessionId);
          resolve();
        };
        spawnedChild.once("error", handleError);
        spawnedChild.once("spawn", handleSpawn);
      });

      spawnedChild.stdin.end(prompt, "utf8");
    } catch (error) {
      this.starting.delete(session.sessionId);
      this.running.delete(session.sessionId);
      if (child) {
        this.children.delete(child);
      }
      throw error;
    }
  }

  async close(): Promise<void> {
    const children = [...this.children];
    this.children.clear();
    this.running.clear();
    this.starting.clear();
    await Promise.allSettled(children.map((child) => this.terminateProcessTree(child)));
  }

  private async terminateProcessTree(child: ChildProcessWithoutNullStreams): Promise<void> {
    if (child.exitCode !== null || child.signalCode !== null) {
      return;
    }
    if (process.platform !== "win32" || child.pid === undefined) {
      child.kill("SIGTERM");
      return;
    }
    await new Promise<void>((resolve) => {
      const killer = spawn(
        "taskkill",
        ["/pid", String(child.pid), "/T", "/F"],
        { stdio: "ignore", windowsHide: true },
      );
      let settled = false;
      const finish = (): void => {
        if (settled) return;
        settled = true;
        resolve();
      };
      killer.once("error", () => {
        child.kill();
        finish();
      });
      killer.once("close", finish);
    });
  }
}
