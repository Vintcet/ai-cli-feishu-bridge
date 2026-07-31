import { randomUUID } from "node:crypto";
import { mkdir, readdir, rm, stat } from "node:fs/promises";
import path from "node:path";

import { FeishuGateway } from "./feishu.js";

export interface IncomingAttachment {
  fileKey: string;
  fileName: string;
  resourceType: "image" | "file";
}

export interface SavedAttachment {
  absolutePath: string;
  fileName: string;
  size: number;
}

export interface ParsedFeishuContent {
  text: string;
  attachments: IncomingAttachment[];
}

export class LocalAttachmentStore {
  private lastPrunedAt = 0;

  constructor(
    private readonly rootDirectory: string,
    private readonly maxBytes: number,
    private readonly maxPerMessage: number,
    private readonly ttlMs: number,
  ) {}

  async download(
    gateway: FeishuGateway,
    messageId: string,
    attachments: IncomingAttachment[],
  ): Promise<SavedAttachment[]> {
    if (attachments.length > this.maxPerMessage) {
      throw new Error(`每条消息最多接收 ${this.maxPerMessage} 个附件。`);
    }
    await this.pruneIfNeeded();
    const month = new Date().toISOString().slice(0, 7);
    const directory = path.join(this.rootDirectory, month);
    await mkdir(directory, { recursive: true });

    const saved: SavedAttachment[] = [];
    try {
      for (const [index, attachment] of attachments.entries()) {
        const safeName = sanitizeFileName(attachment.fileName || defaultFileName(attachment));
        const destinationPath = path.join(
          directory,
          `${sanitizeToken(messageId)}-${index + 1}-${randomUUID().slice(0, 8)}-${safeName}`,
        );
        const size = await gateway.downloadMessageResource(
          messageId,
          attachment.fileKey,
          attachment.resourceType,
          destinationPath,
          this.maxBytes,
        );
        saved.push({ absolutePath: path.resolve(destinationPath), fileName: safeName, size });
      }
      return saved;
    } catch (error) {
      await Promise.allSettled(saved.map((item) => rm(item.absolutePath, { force: true })));
      throw error;
    }
  }

  private async pruneIfNeeded(): Promise<void> {
    const now = Date.now();
    if (now - this.lastPrunedAt < 60 * 60 * 1000) {
      return;
    }
    this.lastPrunedAt = now;
    await pruneDirectory(this.rootDirectory, now - this.ttlMs).catch((error) => {
      console.warn("[attachments] Could not prune old uploads:", error);
    });
  }
}

export function parseFeishuContent(message: Record<string, unknown> | undefined): ParsedFeishuContent {
  const messageType = typeof message?.message_type === "string"
    ? message.message_type
    : "text";
  const content = parseJsonObject(message?.content);
  if (!content) {
    return { text: "", attachments: [] };
  }

  if (messageType === "text") {
    return {
      text: typeof content.text === "string"
        ? stripLeadingMentions(content.text, message?.mentions)
        : "",
      attachments: [],
    };
  }
  if (messageType === "image" && typeof content.image_key === "string") {
    return {
      text: "",
      attachments: [
        {
          fileKey: content.image_key,
          fileName: `feishu-image-${content.image_key.slice(-8)}.jpg`,
          resourceType: "image",
        },
      ],
    };
  }
  if (messageType === "file" && typeof content.file_key === "string") {
    return {
      text: "",
      attachments: [
        {
          fileKey: content.file_key,
          fileName: typeof content.file_name === "string" ? content.file_name : "feishu-file.bin",
          resourceType: "file",
        },
      ],
    };
  }
  if (messageType === "post") {
    return parsePostContent(content);
  }
  return { text: "", attachments: [] };
}

function stripLeadingMentions(text: string, value: unknown): string {
  let normalized = text.trim();
  if (!Array.isArray(value)) {
    return normalized;
  }
  const keys = value
    .map((mention) =>
      mention && typeof mention === "object" && !Array.isArray(mention) &&
          typeof (mention as Record<string, unknown>).key === "string"
        ? String((mention as Record<string, unknown>).key)
        : "",
    )
    .filter(Boolean);
  let changed = true;
  while (changed && normalized) {
    changed = false;
    for (const key of keys) {
      const pattern = new RegExp(`^${escapeRegExp(key)}[\\s:：,，]*`, "u");
      if (pattern.test(normalized)) {
        normalized = normalized.replace(pattern, "").trim();
        changed = true;
      }
    }
  }
  return normalized;
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

export function appendAttachmentsToPrompt(
  prompt: string,
  attachments: SavedAttachment[],
): string {
  if (attachments.length === 0) {
    return prompt;
  }
  const files = attachments
    .map((attachment, index) => `${index + 1}. ${attachment.absolutePath}`)
    .join("；");
  return `飞书附件已保存到本机：${files}。请使用适合的文件读取工具处理这些文件。用户要求：${prompt}`;
}

function parsePostContent(content: Record<string, unknown>): ParsedFeishuContent {
  const texts: string[] = [];
  const attachments: IncomingAttachment[] = [];
  const seenKeys = new Set<string>();

  const walk = (value: unknown): void => {
    if (Array.isArray(value)) {
      value.forEach(walk);
      return;
    }
    if (!value || typeof value !== "object") {
      return;
    }
    const item = value as Record<string, unknown>;
    if (typeof item.text === "string" && item.text.trim()) {
      texts.push(item.text.trim());
    }
    if (typeof item.title === "string" && item.title.trim()) {
      texts.push(item.title.trim());
    }
    if (typeof item.image_key === "string" && !seenKeys.has(item.image_key)) {
      seenKeys.add(item.image_key);
      attachments.push({
        fileKey: item.image_key,
        fileName: `feishu-image-${item.image_key.slice(-8)}.jpg`,
        resourceType: "image",
      });
    }
    if (typeof item.file_key === "string" && !seenKeys.has(item.file_key)) {
      seenKeys.add(item.file_key);
      attachments.push({
        fileKey: item.file_key,
        fileName: typeof item.file_name === "string" ? item.file_name : "feishu-file.bin",
        resourceType: "file",
      });
    }
    for (const nested of Object.values(item)) {
      if (nested !== item.text && nested !== item.title) {
        walk(nested);
      }
    }
  };
  walk(content);
  return { text: texts.join("\n").trim(), attachments };
}

function parseJsonObject(value: unknown): Record<string, unknown> | undefined {
  if (value && typeof value === "object" && !Array.isArray(value)) {
    return value as Record<string, unknown>;
  }
  if (typeof value !== "string") {
    return undefined;
  }
  try {
    const parsed: unknown = JSON.parse(value);
    return parsed && typeof parsed === "object" && !Array.isArray(parsed)
      ? (parsed as Record<string, unknown>)
      : undefined;
  } catch {
    return undefined;
  }
}

function sanitizeFileName(value: string): string {
  const base = path.basename(value).normalize("NFC");
  const cleaned = base
    .replace(/[<>:"/\\|?*\u0000-\u001f]/g, "_")
    .replace(/[. ]+$/g, "")
    .trim();
  return (cleaned || "attachment.bin").slice(0, 120);
}

function sanitizeToken(value: string): string {
  return value.replace(/[^a-zA-Z0-9_-]/g, "").slice(-48) || "message";
}

function defaultFileName(attachment: IncomingAttachment): string {
  return attachment.resourceType === "image" ? "feishu-image.jpg" : "feishu-file.bin";
}

async function pruneDirectory(directory: string, cutoff: number): Promise<void> {
  let entries;
  try {
    entries = await readdir(directory, { withFileTypes: true });
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return;
    throw error;
  }
  for (const entry of entries) {
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      await pruneDirectory(fullPath, cutoff);
      const remaining = await readdir(fullPath).catch(() => ["not-empty"]);
      if (remaining.length === 0) {
        await rm(fullPath, { recursive: true, force: true });
      }
      continue;
    }
    if (!entry.isFile()) continue;
    const fileStat = await stat(fullPath).catch(() => undefined);
    if (fileStat && fileStat.mtimeMs < cutoff) {
      await rm(fullPath, { force: true });
    }
  }
}
