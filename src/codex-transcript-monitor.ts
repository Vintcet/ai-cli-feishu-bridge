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
  activeUntil: number;
  nextScanAt: number;
  fileIdentity?: string;
  stopping?: boolean;
  scanning?: Promise<void>;
}

interface TranscriptFileSnapshot {
  size: number;
  identity?: string;
}

export interface CodexTranscriptMonitorOptions {
  /** 文件活跃时的轮询间隔。 */
  activePollIntervalMs?: number;
  /** 文件持续无变化后的轮询间隔。 */
  idlePollIntervalMs?: number;
  /** 最后一次登记或文件变化后维持活跃轮询的时长。 */
  activeWindowMs?: number;
}

export class CodexTranscriptMonitor {
  private readonly watches = new Map<string, WatchState>();
  private timer: NodeJS.Timeout | undefined;
  private timerDueAt: number | undefined;
  private closed = false;
  private closePromise: Promise<void> | undefined;
  private readonly activePollIntervalMs: number;
  private readonly idlePollIntervalMs: number;
  private readonly activeWindowMs: number;

  constructor(
    private readonly onError: (event: CodexTranscriptErrorEvent) => Promise<void>,
    options: number | CodexTranscriptMonitorOptions = {},
  ) {
    const resolved = typeof options === "number"
      ? { activePollIntervalMs: options }
      : options;
    this.activePollIntervalMs = Math.max(
      50,
      resolved.activePollIntervalMs ?? 750,
    );
    this.idlePollIntervalMs = Math.max(
      this.activePollIntervalMs,
      resolved.idlePollIntervalMs ?? 5_000,
    );
    this.activeWindowMs = Math.max(
      this.activePollIntervalMs,
      resolved.activeWindowMs ?? 30_000,
    );
  }

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
      this.activate(current, true);
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
    const now = Date.now();
    this.watches.set(sessionId, {
      sessionId,
      transcriptPath: normalizedPath,
      offset: initialSnapshot.size,
      carry: Buffer.alloc(0),
      activeUntil: now + this.activeWindowMs,
      nextScanAt: now + this.activePollIntervalMs,
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
    this.scheduleNext();
  }

  async checkNow(): Promise<void> {
    await this.scanStates(
      [...this.watches.values()].filter((state) => !state.stopping),
    );
  }

  close(): Promise<void> {
    if (this.closePromise) return this.closePromise;
    this.closed = true;
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = undefined;
      this.timerDueAt = undefined;
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
    this.scheduleNext();
  }

  private scheduleNext(): void {
    if (this.closed || this.watches.size === 0) {
      if (this.timer) {
        clearTimeout(this.timer);
        this.timer = undefined;
        this.timerDueAt = undefined;
      }
      return;
    }
    const now = Date.now();
    let dueAt = Number.POSITIVE_INFINITY;
    for (const state of this.watches.values()) {
      if (!state.stopping && state.nextScanAt < dueAt) {
        dueAt = state.nextScanAt;
      }
    }
    if (!Number.isFinite(dueAt)) {
      if (this.timer) {
        clearTimeout(this.timer);
        this.timer = undefined;
        this.timerDueAt = undefined;
      }
      return;
    }
    if (this.timer && this.timerDueAt === dueAt) {
      return;
    }
    if (this.timer) {
      clearTimeout(this.timer);
    }
    const delay = Math.max(0, dueAt - now);
    this.timerDueAt = dueAt;
    this.timer = setTimeout(() => {
      this.timer = undefined;
      this.timerDueAt = undefined;
      void this.scanDue()
        .catch((error) => {
          console.error("[transcript] Codex transcript scan failed:", error);
        })
        .finally(() => this.scheduleNext());
    }, delay);
    this.timer.unref?.();
  }

  private async scanDue(): Promise<void> {
    const now = Date.now();
    await this.scanStates(
      [...this.watches.values()].filter(
        (state) => !state.stopping && state.nextScanAt <= now,
      ),
    );
  }

  private async scanStates(states: WatchState[]): Promise<void> {
    await Promise.all(states.map((state) => this.scan(state)));
    const now = Date.now();
    for (const state of states) {
      if (this.watches.get(state.sessionId) !== state || state.stopping) {
        continue;
      }
      state.nextScanAt = now + (
        state.activeUntil > now
          ? this.activePollIntervalMs
          : this.idlePollIntervalMs
      );
    }
    this.scheduleNext();
  }

  private activate(state: WatchState, reschedule = false): void {
    const now = Date.now();
    state.activeUntil = now + this.activeWindowMs;
    if (reschedule) {
      state.nextScanAt = Math.min(
        state.nextScanAt,
        now + this.activePollIntervalMs,
      );
      this.scheduleNext();
    }
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
        this.activate(state);
        state.fileIdentity = identity;
        state.offset = 0;
        state.carry = Buffer.alloc(0);
      } else if (fileSize < state.offset) {
        this.activate(state);
        state.offset = 0;
        state.carry = Buffer.alloc(0);
      }
      if (fileSize === state.offset) {
        return;
      }
      this.activate(state);

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
