import { createHash, randomUUID } from "node:crypto";
import { AsyncLocalStorage } from "node:async_hooks";
import { appendFile, mkdir, rename, rm, stat } from "node:fs/promises";
import path from "node:path";

import type { RuntimeName } from "../domain.js";
import {
  behaviorRecordVersion,
  buildBehaviorProjection,
  type BehaviorOutcome,
  type BehaviorRecord,
  type BehaviorStage,
} from "./behavior-record.js";

interface BehaviorTraceContext {
  traceId: string;
  runtime?: RuntimeName;
  sessionId?: string;
}

export interface BehaviorRecordContext {
  runtime?: RuntimeName;
  sessionId?: string;
  traceId?: string;
  outcome?: BehaviorOutcome;
}

export interface BehaviorRecorderOptions {
  enabled: boolean;
  filePath: string;
  maxBytes?: number;
  maxBackups?: number;
  now?: () => Date;
  recordId?: () => string;
}

const semanticStringKeys = new Set([
  "action",
  "behavior",
  "commandType",
  "decision",
  "eventType",
  "hook_event_name",
  "kind",
  "mode",
  "outcome",
  "permission_mode",
  "reply",
  "role",
  "runtime",
  "source",
  "status",
  "tool_name",
  "type",
]);
const sensitiveKeyPattern =
  /(?:secret|token|password|passwd|api[_-]?key|authorization|cookie|private[_-]?key)/iu;
const delimitedIdentifierKeyPattern =
  /(?:^|[_-])(?:id|uuid|open[_-]?id|chat[_-]?id|message[_-]?id|request[_-]?id|session[_-]?id|turn[_-]?id|trace[_-]?id|correlation[_-]?id|tool[_-]?use[_-]?id)$/iu;
const camelCaseIdentifierKeyPattern = /(?:Id|ID|Uuid|UUID)$/u;
const pathKeyPattern =
  /(?:cwd|path|directory|worktree|file|transcript|workspace|root)$/iu;
const urlKeyPattern = /(?:url|endpoint|host)$/iu;
const maximumDepth = 10;
const maximumObjectKeys = 100;
const maximumArrayItems = 100;

export class BehaviorRecorder {
  private readonly traceContext = new AsyncLocalStorage<BehaviorTraceContext>();
  private writeChain: Promise<void> = Promise.resolve();
  private closePromise: Promise<void> | undefined;
  private writeFailureReported = false;

  constructor(private readonly options: BehaviorRecorderOptions) {}

  get enabled(): boolean {
    return this.options.enabled;
  }

  record(
    stage: BehaviorStage,
    kind: string,
    value: unknown,
    context: BehaviorRecordContext = {},
  ): void {
    if (!this.enabled || this.closePromise) return;
    try {
      const active = this.traceContext.getStore();
      const recordId = this.options.recordId?.() ?? randomUUID();
      const runtime = context.runtime ?? active?.runtime;
      const sessionId = context.sessionId ?? active?.sessionId;
      const outcome = context.outcome ?? "observed";
      const observed = sanitizeBehaviorValue(value);
      const record: BehaviorRecord = {
        recordVersion: behaviorRecordVersion,
        recordId,
        recordedAt: (this.options.now?.() ?? new Date()).toISOString(),
        source: "node",
        stage,
        kind,
        traceId: reference(
          "trace",
          context.traceId ?? active?.traceId ?? recordId,
        ),
        ...(runtime ? { runtime } : {}),
        ...(sessionId ? { sessionRef: reference("session", sessionId) } : {}),
        outcome,
        observed,
        expectedProjection: buildBehaviorProjection({
          stage,
          kind,
          runtime,
          outcome,
          observed,
        }),
      };
      const line = `${JSON.stringify(record)}\n`;
      this.writeChain = this.writeChain
        .then(() => this.appendLine(line))
        .catch((error) => this.reportFailure(error));
    } catch (error) {
      this.reportFailure(error);
    }
  }

  async capture<T>(
    stage: BehaviorStage,
    kind: string,
    input: unknown,
    operation: () => T | Promise<T>,
    context: BehaviorRecordContext = {},
  ): Promise<T> {
    if (!this.enabled) return await operation();
    const parent = this.traceContext.getStore();
    const trace: BehaviorTraceContext = {
      traceId: context.traceId ?? parent?.traceId ?? randomUUID(),
      runtime: context.runtime ?? parent?.runtime,
      sessionId: context.sessionId ?? parent?.sessionId,
    };
    return await this.traceContext.run(trace, async () => {
      try {
        const result = await operation();
        this.record(
          stage,
          kind,
          { input, result },
          { ...context, outcome: "succeeded" },
        );
        return result;
      } catch (error) {
        this.record(
          stage,
          kind,
          { input, error: error instanceof Error ? error.message : String(error) },
          { ...context, outcome: "failed" },
        );
        throw error;
      }
    });
  }

  async flush(): Promise<void> {
    await this.writeChain;
  }

  async close(): Promise<void> {
    if (!this.closePromise) {
      this.closePromise = this.flush();
    }
    await this.closePromise;
  }

  private async appendLine(line: string): Promise<void> {
    await mkdir(path.dirname(this.options.filePath), { recursive: true });
    const maxBytes = Math.max(1, this.options.maxBytes ?? 10 * 1024 * 1024);
    let size = 0;
    try {
      size = (await stat(this.options.filePath)).size;
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
    }
    if (size > 0 && size + Buffer.byteLength(line) > maxBytes) {
      await rotateFiles(this.options.filePath, Math.max(0, this.options.maxBackups ?? 3));
    }
    await appendFile(this.options.filePath, line, "utf8");
  }

  private reportFailure(error: unknown): void {
    if (this.writeFailureReported) return;
    this.writeFailureReported = true;
    console.error(
      "[migration] Behavior recording failed; bridge execution continues:",
      error,
    );
  }
}

export function sanitizeBehaviorValue(
  value: unknown,
  key = "",
  depth = 0,
  seen = new Set<object>(),
): unknown {
  if (sensitiveKeyPattern.test(key)) return "[redacted]";
  if (value === null || value === undefined) return value ?? null;
  if (typeof value === "string") {
    if (isIdentifierKey(key)) return reference("id", value);
    if (pathKeyPattern.test(key)) return reference("path", value);
    if (urlKeyPattern.test(key)) return reference("endpoint", value);
    if (semanticStringKeys.has(key)) return value.slice(0, 160);
    return textSummary(value);
  }
  if (typeof value === "number" || typeof value === "boolean") {
    return isIdentifierKey(key) ? reference("id", String(value)) : value;
  }
  if (typeof value === "bigint") return value.toString();
  if (typeof value !== "object") return { $type: typeof value };
  if (Buffer.isBuffer(value)) {
    return { $redacted: "binary", length: value.length };
  }
  if (depth >= maximumDepth || seen.has(value)) {
    return { $truncated: true };
  }
  seen.add(value);
  try {
    if (Array.isArray(value)) {
      const items = value
        .slice(0, maximumArrayItems)
        .map((item) => sanitizeBehaviorValue(item, key, depth + 1, seen));
      if (value.length > maximumArrayItems) {
        items.push({ $remaining: value.length - maximumArrayItems });
      }
      return items;
    }
    const entries = Object.entries(value as Record<string, unknown>)
      .sort(([left], [right]) => left.localeCompare(right, "en"))
      .slice(0, maximumObjectKeys)
      .map(([childKey, child]) => [
        childKey,
        sanitizeBehaviorValue(child, childKey, depth + 1, seen),
      ]);
    const result = Object.fromEntries(entries) as Record<string, unknown>;
    const totalKeys = Object.keys(value).length;
    if (totalKeys > maximumObjectKeys) {
      result.$remainingKeys = totalKeys - maximumObjectKeys;
    }
    return result;
  } finally {
    seen.delete(value);
  }
}

function isIdentifierKey(key: string): boolean {
  return (
    delimitedIdentifierKeyPattern.test(key) ||
    camelCaseIdentifierKeyPattern.test(key)
  );
}

function textSummary(value: string): Record<string, unknown> {
  return {
    $redacted: "text",
    length: value.length,
    sha256: createHash("sha256").update(value).digest("hex").slice(0, 16),
  };
}

function reference(namespace: string, value: string): string {
  const digest = createHash("sha256")
    .update(`${namespace}\0${value}`)
    .digest("hex")
    .slice(0, 16);
  return `${namespace}:${digest}`;
}

async function rotateFiles(filePath: string, backups: number): Promise<void> {
  if (backups === 0) {
    await rm(filePath, { force: true });
    return;
  }
  for (let index = backups; index >= 1; index -= 1) {
    const source = index === 1 ? filePath : `${filePath}.${index - 1}`;
    const destination = `${filePath}.${index}`;
    await rm(destination, { force: true });
    try {
      await rename(source, destination);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
    }
  }
}
