import { lstat, mkdir, realpath } from "node:fs/promises";
import path from "node:path";

import type { RuntimeName } from "./domain.js";

export interface AliasCommand {
  targetKind?: "short" | "alias";
  target?: string;
  alias?: string;
}

export interface NewRuntimeCommand {
  runtime: RuntimeName;
  projectName: string;
}

export function parseBindCommand(
  text: string,
  command: string,
): { matched: boolean; code?: string } {
  if (text === command) {
    return { matched: true };
  }
  const prefix = `${command} `;
  if (!text.startsWith(prefix)) {
    return { matched: false };
  }
  const code = text.slice(prefix.length).trim();
  return { matched: true, code: code || undefined };
}

export function parseNewRuntimeCommand(
  text: string,
): NewRuntimeCommand | undefined {
  const match = text.match(
    /^新建\s+(claude\s+code|open\s+code|codex|claude|claudecode|opencode)\s+([\s\S]+)$/iu,
  );
  if (!match?.[1] || !match[2]?.trim()) {
    return undefined;
  }
  const runtimeText = match[1].replace(/\s+/gu, "").toLocaleLowerCase("en-US");
  const runtime: RuntimeName = runtimeText === "codex"
    ? "codex"
    : runtimeText === "opencode"
    ? "opencode"
    : "claudecode";
  return {
    runtime,
    projectName: stripMatchingQuotes(match[2].trim()).normalize("NFC"),
  };
}

export function newRuntimeCommandUsage(): string {
  return "用法：新建 codex 项目名\n也支持：新建 claude 项目名、新建 opencode 项目名。\n项目会放在电脑端“设置”中的默认工作区；目录不存在时自动创建。";
}

export function projectDirectoryNameValidationError(
  value: string,
): string | undefined {
  const name = value.trim().normalize("NFC");
  if (!name) {
    return "项目名不能为空。";
  }
  if (Array.from(name).length > 80) {
    return "项目名最多 80 个字符。";
  }
  if (name === "." || name === "..") {
    return "不能使用点目录。";
  }
  if (/[<>:"/\\|?*\u0000-\u001f]/u.test(name)) {
    return "不能包含斜杠、盘符或 Windows 文件名保留字符。";
  }
  if (/[. ]$/u.test(name)) {
    return "不能以句点或空格结尾。";
  }
  if (/^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/iu.test(name)) {
    return "不能使用 Windows 保留名称。";
  }
  return undefined;
}

export async function prepareProjectDirectory(
  workspaceRoot: string,
  projectName: string,
): Promise<{ cwd: string; created: boolean }> {
  const root = path.resolve(workspaceRoot);
  await mkdir(root, { recursive: true });
  const rootRealPath = await realpath(root);
  const projectPath = path.join(root, projectName.trim().normalize("NFC"));
  let created = false;
  try {
    await mkdir(projectPath);
    created = true;
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== "EEXIST") {
      throw error;
    }
  }
  const projectInfo = await lstat(projectPath);
  if (!projectInfo.isDirectory() || projectInfo.isSymbolicLink()) {
    throw new Error("同名路径不是可用的普通文件夹。");
  }
  const projectRealPath = await realpath(projectPath);
  if (!isPathWithinRoot(rootRealPath, projectRealPath)) {
    throw new Error("项目目录超出了默认工作区。");
  }
  return { cwd: projectRealPath, created };
}

export function parsePromptDirectives(text: string): {
  prompt: string;
  queue: boolean;
  fileReturn: boolean;
} {
  let prompt = text.trim();
  let queue = false;
  let fileReturn = false;
  for (let index = 0; index < 3; index += 1) {
    const queueMatch = prompt.match(/^(?:排队|\/queue|queue)\s+([\s\S]+)$/iu);
    if (queueMatch?.[1]) {
      queue = true;
      prompt = queueMatch[1].trim();
      continue;
    }
    const fileMatch = prompt.match(
      /^(?:发文件|\/sendfile|sendfile)\s+([\s\S]+)$/iu,
    );
    if (fileMatch?.[1]) {
      fileReturn = true;
      prompt = fileMatch[1].trim();
      continue;
    }
    break;
  }
  return { prompt, queue, fileReturn };
}

export function parseExplicitSession(
  text: string,
): { kind: "short"; token: string; prompt: string } | undefined {
  const match = text.match(/^#([a-zA-Z0-9]{4,32})\s+([\s\S]+)$/);
  if (!match?.[1] || !match[2]?.trim()) {
    return undefined;
  }
  return {
    kind: "short",
    token: match[1].toLowerCase(),
    prompt: match[2].trim(),
  };
}

export function parseExplicitAlias(
  text: string,
): { kind: "alias"; token: string; prompt: string } | undefined {
  const match = text.match(/^@([^\s@#]+)\s+([\s\S]+)$/u);
  if (!match?.[1] || !match[2]?.trim()) {
    return undefined;
  }
  return { kind: "alias", token: match[1], prompt: match[2].trim() };
}

export function parseAliasCommand(text: string): AliasCommand | undefined {
  if (text === "别名") {
    return {};
  }
  const match = text.match(/^别名\s+([#@])([^\s#@]+)(?:\s+([\s\S]+))?$/u);
  if (!match?.[1] || !match[2]) {
    return undefined;
  }
  if (match[1] === "#" && !/^[a-zA-Z0-9]{4,32}$/.test(match[2])) {
    return undefined;
  }
  return {
    targetKind: match[1] === "#" ? "short" : "alias",
    target: match[1] === "#" ? match[2].toLowerCase() : match[2],
    alias: match[3]?.trim(),
  };
}

export function aliasCommandUsage(): string {
  return "设置：别名 #短ID 名称\n清除：别名 #短ID 清除\n也可用旧别名定位：别名 @旧别名 新名称\n回复：@名称 你的内容\n规则：1–20 个字符，可用中文、字母、数字、下划线和短横线。";
}

function stripMatchingQuotes(value: string): string {
  if (value.length >= 2) {
    const first = value[0];
    const last = value.at(-1);
    if ((first === '"' && last === '"') || (first === "'" && last === "'")) {
      return value.slice(1, -1).trim();
    }
  }
  return value;
}

function isPathWithinRoot(root: string, target: string): boolean {
  const relative = path.relative(root, target);
  return relative !== "" && relative !== ".." &&
    !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative);
}
