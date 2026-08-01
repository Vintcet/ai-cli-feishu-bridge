import { open } from "node:fs/promises";
import path from "node:path";

const maxTranscriptTailBytes = 4 * 1024 * 1024;

export interface ClaudeAssistantMessage {
  text: string;
  turnId?: string;
}

export function extractLastClaudeAssistantMessage(
  content: string,
): ClaudeAssistantMessage | null {
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

    const nested = entry.message && typeof entry.message === "object" &&
        !Array.isArray(entry.message)
      ? entry.message as Record<string, unknown>
      : undefined;
    const message = nested ?? entry;
    const role = message.role ?? entry.type;
    if (role !== "assistant") continue;

    const text = assistantText(message.content);
    if (!text) continue;
    const turnId = firstString(entry.uuid, message.id, entry.message_id);
    return { text, ...(turnId ? { turnId } : {}) };
  }
  return null;
}

export async function readLastClaudeAssistantMessage(
  transcriptPath: string,
): Promise<ClaudeAssistantMessage | null> {
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
    return extractLastClaudeAssistantMessage(content);
  } catch {
    return null;
  } finally {
    await file?.close().catch(() => undefined);
  }
}

function assistantText(content: unknown): string | undefined {
  if (typeof content === "string") {
    return content.trim() || undefined;
  }
  if (!Array.isArray(content)) return undefined;
  const parts = content.flatMap((block): string[] => {
    if (!block || typeof block !== "object" || Array.isArray(block)) return [];
    const item = block as Record<string, unknown>;
    return item.type === "text" && typeof item.text === "string" && item.text.trim()
      ? [item.text.trim()]
      : [];
  });
  return parts.length > 0 ? parts.join("\n\n") : undefined;
}

function firstString(...values: unknown[]): string | undefined {
  return values.find((value): value is string => typeof value === "string" && value.length > 0);
}
