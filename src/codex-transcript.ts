import { open } from "node:fs/promises";
import path from "node:path";

const maxTranscriptTailBytes = 4 * 1024 * 1024;

export interface CodexTurnCompletion {
  turnId?: string;
  assistantMessage?: string;
  error?: string;
  errorCode?: string;
}

export function extractCodexTurnCompletion(
  content: string,
  expectedTurnId?: string,
): CodexTurnCompletion | null {
  const lines = content.split(/\r?\n/u).reverse();
  for (const line of lines) {
    if (!line.trim()) continue;
    let entry: Record<string, unknown>;
    try {
      const parsed: unknown = JSON.parse(line);
      if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) continue;
      entry = parsed as Record<string, unknown>;
    } catch {
      continue;
    }

    const payload = objectValue(entry.payload);
    const event = entry.type === "event_msg" && payload ? payload : entry;
    if (event.type !== "task_complete") continue;

    const turnId = stringValue(event.turn_id);
    if (expectedTurnId && turnId !== expectedTurnId) continue;

    const assistantMessage = stringValue(event.last_agent_message);
    const structuredError = completionError(event.error);
    return {
      ...(turnId ? { turnId } : {}),
      ...(assistantMessage ? { assistantMessage } : {}),
      ...(structuredError?.message ? { error: structuredError.message } : {}),
      ...(structuredError?.code ? { errorCode: structuredError.code } : {}),
    };
  }
  return null;
}

export async function readCodexTurnCompletion(
  transcriptPath: string,
  expectedTurnId?: string,
): Promise<CodexTurnCompletion | null> {
  if (!path.isAbsolute(transcriptPath) || path.extname(transcriptPath).toLowerCase() !== ".jsonl") {
    return null;
  }
  let file;
  try {
    file = await open(transcriptPath, "r");
    const stat = await file.stat();
    const length = Math.min(stat.size, maxTranscriptTailBytes);
    const start = Math.max(0, stat.size - length);
    const buffer = Buffer.alloc(length);
    const { bytesRead } = await file.read(buffer, 0, length, start);
    let content = buffer.toString("utf8", 0, bytesRead);
    if (start > 0) {
      const firstNewline = content.indexOf("\n");
      content = firstNewline >= 0 ? content.slice(firstNewline + 1) : "";
    }
    return extractCodexTurnCompletion(content, expectedTurnId);
  } catch {
    return null;
  } finally {
    await file?.close().catch(() => undefined);
  }
}

function completionError(value: unknown): { message?: string; code?: string } | undefined {
  if (typeof value === "string") {
    const message = value.trim();
    return message ? { message } : undefined;
  }
  const item = objectValue(value);
  if (!item) return undefined;
  const message = stringValue(item.message);
  const code = stringValue(item.codex_error_info, item.code, item.type);
  if (!message && !code) return undefined;
  return {
    ...(message ? { message } : { message: `Codex error: ${code}` }),
    ...(code ? { code } : {}),
  };
}

function objectValue(value: unknown): Record<string, unknown> | undefined {
  return value && typeof value === "object" && !Array.isArray(value)
    ? value as Record<string, unknown>
    : undefined;
}

function stringValue(...values: unknown[]): string | undefined {
  for (const value of values) {
    if (typeof value !== "string") continue;
    const normalized = value.trim();
    if (normalized) return normalized;
  }
  return undefined;
}
