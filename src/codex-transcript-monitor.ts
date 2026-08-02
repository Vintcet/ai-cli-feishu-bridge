import { open, stat } from "node:fs/promises";
import path from "node:path";

import { parseCodexTurnCompletionLine } from "./codex-transcript.js";

const maxReadBytes = 4 * 1024 * 1024;

export interface CodexTranscriptErrorEvent {
  sessionId: string;
  turnId: string;
  transcriptPath: string;
  error: string;
  errorCode?: string;
}

interface WatchState {
  sessionId: string;
  transcriptPath: string;
  offset: number;
  carry: Buffer;
  scanning?: Promise<void>;
}

export class CodexTranscriptMonitor {
  private readonly watches = new Map<string, WatchState>();
  private timer: NodeJS.Timeout | undefined;
  private closed = false;

  constructor(
    private readonly onError: (event: CodexTranscriptErrorEvent) => Promise<void>,
    private readonly pollIntervalMs = 750,
  ) {}

  async watch(sessionId: string, transcriptPath: string | null | undefined): Promise<boolean> {
    if (
      this.closed ||
      !transcriptPath ||
      !path.isAbsolute(transcriptPath) ||
      path.extname(transcriptPath).toLowerCase() !== ".jsonl"
    ) {
      return false;
    }
    const normalizedPath = path.resolve(transcriptPath);
    const current = this.watches.get(sessionId);
    if (current?.transcriptPath === normalizedPath) {
      return true;
    }

    const initialOffset = await stat(normalizedPath)
      .then((value) => value.size)
      .catch((error: NodeJS.ErrnoException) => {
        if (error.code === "ENOENT") return 0;
        console.warn(
          "[transcript] Could not watch " + path.basename(normalizedPath) + ":",
          error,
        );
        return undefined;
      });
    if (initialOffset === undefined) {
      return false;
    }
    if (this.closed) {
      return false;
    }
    this.watches.set(sessionId, {
      sessionId,
      transcriptPath: normalizedPath,
      offset: initialOffset,
      carry: Buffer.alloc(0),
    });
    this.start();
    return true;
  }

  async unwatch(sessionId: string): Promise<void> {
    const state = this.watches.get(sessionId);
    if (!state) return;
    if (!this.closed) {
      await this.scan(state);
    }
    if (this.watches.get(sessionId) !== state) {
      return;
    }
    this.watches.delete(sessionId);
    if (this.watches.size === 0 && this.timer) {
      clearInterval(this.timer);
      this.timer = undefined;
    }
  }

  async checkNow(): Promise<void> {
    await Promise.all([...this.watches.values()].map((state) => this.scan(state)));
  }

  async close(): Promise<void> {
    if (this.closed) return;
    this.closed = true;
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = undefined;
    }
    await Promise.allSettled(
      [...this.watches.values()].map((state) => this.scan(state)),
    );
    this.watches.clear();
  }

  private start(): void {
    if (this.timer || this.closed) return;
    this.timer = setInterval(() => {
      void this.checkNow().catch((error) => {
        console.error("[transcript] Codex transcript scan failed:", error);
      });
    }, Math.max(50, this.pollIntervalMs));
    this.timer.unref?.();
  }

  private scan(state: WatchState): Promise<void> {
    if (state.scanning) {
      return state.scanning;
    }
    const scanning = this.scanOnce(state)
      .catch((error: NodeJS.ErrnoException) => {
        if (error.code !== "ENOENT") {
          console.warn(
            `[transcript] Could not read ${path.basename(state.transcriptPath)}:`,
            error,
          );
        }
      })
      .finally(() => {
        state.scanning = undefined;
      });
    state.scanning = scanning;
    return scanning;
  }

  private async scanOnce(state: WatchState): Promise<void> {
    const fileStat = await stat(state.transcriptPath);
    if (fileStat.size < state.offset) {
      state.offset = 0;
      state.carry = Buffer.alloc(0);
    }
    if (fileStat.size === state.offset) {
      return;
    }

    let start = state.offset;
    if (fileStat.size - start > maxReadBytes) {
      start = fileStat.size - maxReadBytes;
      state.carry = Buffer.alloc(0);
    }
    const length = fileStat.size - start;
    const file = await open(state.transcriptPath, "r");
    let chunk: Buffer;
    try {
      const buffer = Buffer.alloc(length);
      const { bytesRead } = await file.read(buffer, 0, length, start);
      state.offset = start + bytesRead;
      chunk = buffer.subarray(0, bytesRead);
    } finally {
      await file.close().catch(() => undefined);
    }

    const content = state.carry.length > 0
      ? Buffer.concat([state.carry, chunk])
      : chunk;
    const lastNewline = content.lastIndexOf(0x0a);
    if (lastNewline < 0) {
      state.carry = Buffer.from(
        content.length > maxReadBytes
          ? content.subarray(content.length - maxReadBytes)
          : content,
      );
      return;
    }
    state.carry = Buffer.from(content.subarray(lastNewline + 1));
    const lines = content
      .subarray(0, lastNewline + 1)
      .toString("utf8")
      .split(/\r?\n/u);
    lines.pop();
    for (const [index, line] of lines.entries()) {
      const completion = parseCodexTurnCompletionLine(line);
      if (!completion?.error) continue;
      const turnId = completion.turnId || `transcript-${state.offset}-${index}`;
      try {
        await this.onError({
          sessionId: state.sessionId,
          turnId,
          transcriptPath: state.transcriptPath,
          error: completion.error,
          ...(completion.errorCode ? { errorCode: completion.errorCode } : {}),
        });
      } catch (error) {
        console.error(`[transcript] Could not handle Codex error for ${turnId}:`, error);
      }
    }
  }
}
