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
  fileIdentity?: string;
  stopping?: boolean;
  scanning?: Promise<void>;
}

interface TranscriptFileSnapshot {
  size: number;
  identity?: string;
}

export class CodexTranscriptMonitor {
  private readonly watches = new Map<string, WatchState>();
  private timer: NodeJS.Timeout | undefined;
  private closed = false;
  private closePromise: Promise<void> | undefined;

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
    if (current?.transcriptPath === normalizedPath && !current.stopping) {
      return true;
    }

    const initialSnapshot = await stat(normalizedPath, { bigint: true })
      .then((value): TranscriptFileSnapshot => ({
        size: Number(value.size),
        identity: transcriptFileIdentity(value.dev, value.ino, value.birthtimeMs),
      }))
      .catch((error: NodeJS.ErrnoException): TranscriptFileSnapshot | undefined => {
        if (error.code === "ENOENT") return { size: 0 };
        console.warn(
          "[transcript] Could not watch " + path.basename(normalizedPath) + ":",
          error,
        );
        return undefined;
      });
    if (initialSnapshot === undefined) {
      return false;
    }
    if (this.closed) {
      return false;
    }
    this.watches.set(sessionId, {
      sessionId,
      transcriptPath: normalizedPath,
      offset: initialSnapshot.size,
      carry: Buffer.alloc(0),
      fileIdentity: initialSnapshot.identity,
    });
    this.start();
    return true;
  }

  async unwatch(sessionId: string): Promise<void> {
    const state = this.watches.get(sessionId);
    if (!state) return;
    state.stopping = true;
    await this.scanAfterCurrent(state);
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
    await Promise.all(
      [...this.watches.values()]
        .filter((state) => !state.stopping)
        .map((state) => this.scan(state)),
    );
  }

  close(): Promise<void> {
    if (this.closePromise) return this.closePromise;
    this.closed = true;
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = undefined;
    }
    const states = [...this.watches.values()];
    states.forEach((state) => {
      state.stopping = true;
    });
    this.closePromise = Promise.allSettled(
      states.map((state) => this.scanAfterCurrent(state)),
    ).then(() => {
      this.watches.clear();
    });
    return this.closePromise;
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

  private async scanAfterCurrent(state: WatchState): Promise<void> {
    if (state.scanning) {
      await state.scanning;
    }
    await this.scan(state);
  }

  private async scanOnce(state: WatchState): Promise<void> {
    const file = await open(state.transcriptPath, "r");
    try {
      const fileStat = await file.stat({ bigint: true });
      const fileSize = Number(fileStat.size);
      const identity = transcriptFileIdentity(
        fileStat.dev,
        fileStat.ino,
        fileStat.birthtimeMs,
      );
      if (state.fileIdentity !== identity) {
        state.fileIdentity = identity;
        state.offset = 0;
        state.carry = Buffer.alloc(0);
      } else if (fileSize < state.offset) {
        state.offset = 0;
        state.carry = Buffer.alloc(0);
      }
      if (fileSize === state.offset) {
        return;
      }

      let start = state.offset;
      if (fileSize - start > maxReadBytes) {
        start = fileSize - maxReadBytes;
        state.carry = Buffer.alloc(0);
      }
      const length = fileSize - start;
      const buffer = Buffer.alloc(length);
      const { bytesRead } = await file.read(buffer, 0, length, start);
      state.offset = start + bytesRead;
      await this.processChunk(state, buffer.subarray(0, bytesRead));
    } finally {
      await file.close().catch(() => undefined);
    }
  }

  private async processChunk(state: WatchState, chunk: Buffer): Promise<void> {
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

function transcriptFileIdentity(dev: bigint, ino: bigint, birthtimeMs: bigint): string {
  return `${dev}:${ino}:${birthtimeMs}`;
}
