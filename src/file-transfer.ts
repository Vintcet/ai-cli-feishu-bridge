import { realpath, stat } from "node:fs/promises";
import path from "node:path";

export interface BridgeFileDirectiveResult {
  displayMessage: string;
  paths: string[];
}

export interface ValidatedBridgeFile {
  path: string;
  size: number;
}

const allowedExtensions = new Set([
  ".bmp",
  ".csv",
  ".doc",
  ".docx",
  ".gif",
  ".ico",
  ".jpeg",
  ".jpg",
  ".json",
  ".log",
  ".md",
  ".mp4",
  ".pdf",
  ".png",
  ".ppt",
  ".pptx",
  ".tif",
  ".tiff",
  ".txt",
  ".webp",
  ".xls",
  ".xlsx",
  ".zip",
]);

export function addFileReturnInstruction(prompt: string): string {
  return `${prompt}\n\n用户明确要求把最终文件发回飞书。请把生成文件保存到当前项目目录内，并在最终回复中为每个文件单独输出一行：BRIDGE_SEND_FILE: 绝对路径。最多 3 个文件；不要声明项目目录外的文件。`;
}

export function extractBridgeFileDirectives(message: string): BridgeFileDirectiveResult {
  const paths: string[] = [];
  const kept: string[] = [];
  for (const line of message.split(/\r?\n/)) {
    const match = line.match(/^\s*BRIDGE_SEND_FILE:\s*(.+?)\s*$/i);
    if (!match?.[1]) {
      kept.push(line);
      continue;
    }
    const candidate = stripWrappingQuotes(match[1].trim());
    if (candidate && paths.length < 3 && !paths.includes(candidate)) {
      paths.push(candidate);
    }
  }
  return {
    displayMessage: kept.join("\n").replace(/\n{3,}/g, "\n\n").trim(),
    paths,
  };
}

export async function validateBridgeFile(
  candidate: string,
  cwd: string,
  maxBytes: number,
): Promise<ValidatedBridgeFile> {
  if (!path.isAbsolute(candidate)) {
    throw new Error("回传路径不是绝对路径。");
  }
  const [resolvedFile, resolvedRoot] = await Promise.all([
    realpath(candidate),
    realpath(cwd),
  ]);
  if (!isInside(resolvedRoot, resolvedFile)) {
    throw new Error("回传文件不在当前项目目录内。");
  }
  const extension = path.extname(resolvedFile).toLowerCase();
  if (!allowedExtensions.has(extension)) {
    throw new Error(`不允许回传 ${extension || "无扩展名"} 文件。`);
  }
  const fileStat = await stat(resolvedFile);
  if (!fileStat.isFile()) {
    throw new Error("回传路径不是普通文件。");
  }
  if (fileStat.size <= 0 || fileStat.size > maxBytes) {
    throw new Error(`回传文件为空或超过 ${formatBytes(maxBytes)}。`);
  }
  return { path: resolvedFile, size: fileStat.size };
}

function isInside(root: string, candidate: string): boolean {
  const relative = path.relative(root, candidate);
  return relative !== "" && !relative.startsWith("..") && !path.isAbsolute(relative);
}

function stripWrappingQuotes(value: string): string {
  if (
    value.length >= 2 &&
    ((value.startsWith('"') && value.endsWith('"')) ||
      (value.startsWith("'") && value.endsWith("'")))
  ) {
    return value.slice(1, -1).trim();
  }
  return value;
}

function formatBytes(bytes: number): string {
  return `${Math.round(bytes / (1024 * 1024))} MiB`;
}
