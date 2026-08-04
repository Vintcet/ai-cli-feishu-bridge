import path from "node:path";

export type ApprovalRiskLevel = "low" | "high";

export interface ApprovalRiskAssessment {
  level: ApprovalRiskLevel;
  reason: string;
}

interface ApprovalRiskInput {
  toolName: string;
  toolInput: unknown;
  cwd: string;
}

interface CollectedInput {
  text: string[];
  paths: string[];
  length: number;
}

const maximumCollectedLength = 64 * 1024;

const highRiskToolPatterns: Array<[RegExp, string]> = [
  [
    /(?:delete|remove|erase|destroy|format|wipe|kill|terminate|shutdown|reboot)/iu,
    "工具本身具有删除、终止或系统控制能力",
  ],
  [
    /(?:publish|deploy|release|upload|send[_-]?(?:message|mail)|purchase|payment)/iu,
    "工具会产生明显的外部副作用",
  ],
];

const highRiskContentPatterns: Array<[RegExp, string]> = [
  [
    /(?:^|[\s;&|])(?:rm|rmdir|del|erase|remove-item|clear-content|shred|unlink)(?:\s|$)/iu,
    "命令包含删除文件或目录操作",
  ],
  [
    /(?:shutil\.rmtree|os\.(?:remove|unlink|rmdir)|fs\.(?:rm|rmdir|unlink)(?:sync)?\s*\()/iu,
    "脚本包含删除文件或目录操作",
  ],
  [
    /\*\*\*\s+delete\s+file\s*:/iu,
    "补丁会删除文件",
  ],
  [
    /\bgit\s+(?:reset\s+--hard|clean\b|checkout\s+--|restore\b|branch\s+-D\b|push\b|rebase\b)/iu,
    "命令包含高影响 Git 操作",
  ],
  [
    /\b(?:npm|pnpm|yarn|pip|pipx|gem|cargo)\s+(?:publish|install|add|remove|uninstall|update|upgrade)\b/iu,
    "命令会安装、删除或发布依赖包",
  ],
  [
    /\b(?:curl|wget|invoke-webrequest|invoke-restmethod|ssh|scp|sftp|rsync|ftp)\b/iu,
    "命令会访问网络或远程主机",
  ],
  [
    /\b(?:kubectl|helm|terraform|ansible|aws|az|gcloud|docker|podman)\b/iu,
    "命令会操作容器、云端或基础设施资源",
  ],
  [
    /\b(?:sudo|runas|chmod|chown|icacls|takeown|set-executionpolicy)\b/iu,
    "命令会提升权限或修改访问控制",
  ],
  [
    /\b(?:shutdown|reboot|restart-computer|stop-computer|format|mkfs|diskpart|bcdedit)\b/iu,
    "命令会修改磁盘或系统启动状态",
  ],
  [
    /\b(?:taskkill|stop-process|kill\s+-?9|systemctl|service|sc(?:\.exe)?\s+(?:delete|stop)|schtasks)\b/iu,
    "命令会终止进程或修改系统服务/计划任务",
  ],
  [
    /\b(?:reg(?:\.exe)?\s+(?:add|delete)|set-itemproperty|new-itemproperty|remove-itemproperty)\b/iu,
    "命令会修改系统注册表或持久配置",
  ],
  [
    /\b(?:drop\s+(?:database|table)|truncate\s+table|delete\s+from)\b/iu,
    "命令包含破坏性数据库操作",
  ],
  [
    /(?:powershell|pwsh)\b[^\r\n]*(?:-(?:enc|encodedcommand)\b|invoke-expression\b|\biex\b)/iu,
    "命令使用编码或动态执行脚本",
  ],
  [
    /(?:^|[\\/])(?:\.ssh|\.aws|\.azure|\.kube|\.gnupg|credentials?|secrets?|private[_-]?keys?)(?:[\\/]|$)/iu,
    "请求会访问凭据、密钥或敏感配置",
  ],
  [
    /(?:^|[\\/])\.env(?:\.[^\\/\s]+)?(?:$|[\s"'])/iu,
    "请求会访问环境密钥文件",
  ],
];

export function assessApprovalRisk(
  input: ApprovalRiskInput,
): ApprovalRiskAssessment {
  for (const [pattern, reason] of highRiskToolPatterns) {
    if (pattern.test(input.toolName)) {
      return { level: "high", reason };
    }
  }

  const collected: CollectedInput = { text: [], paths: [], length: 0 };
  collectInput(input.toolInput, undefined, collected, new Set(), 0);
  const searchable = `${input.toolName}\n${collected.text.join("\n")}`;
  for (const [pattern, reason] of highRiskContentPatterns) {
    if (pattern.test(searchable)) {
      return { level: "high", reason };
    }
  }

  for (const candidate of collected.paths) {
    if (isPathOutsideWorkspace(candidate, input.cwd)) {
      return {
        level: "high",
        reason: "请求会访问当前项目目录之外的路径",
      };
    }
  }

  return { level: "low", reason: "未命中高风险命令或路径规则" };
}

function collectInput(
  value: unknown,
  key: string | undefined,
  collected: CollectedInput,
  seen: Set<object>,
  depth: number,
): void {
  if (depth > 12 || collected.length >= maximumCollectedLength) {
    return;
  }
  if (typeof value === "string") {
    const remaining = maximumCollectedLength - collected.length;
    const text = value.slice(0, remaining);
    collected.text.push(text);
    collected.length += text.length;
    if (key && isPathKey(key)) {
      collected.paths.push(value);
    }
    return;
  }
  if (!value || typeof value !== "object" || seen.has(value)) {
    return;
  }
  seen.add(value);
  if (Array.isArray(value)) {
    for (const item of value) {
      collectInput(item, key, collected, seen, depth + 1);
    }
    return;
  }
  for (const [childKey, childValue] of Object.entries(value)) {
    collectInput(childValue, childKey, collected, seen, depth + 1);
  }
}

function isPathKey(key: string): boolean {
  return /(?:^|_)(?:path|file|filename|directory|dir|cwd|target|destination|source)$/iu
    .test(key);
}

function isPathOutsideWorkspace(candidate: string, cwd: string): boolean {
  const trimmed = candidate.trim();
  if (
    !trimmed ||
    trimmed.includes("\n") ||
    /^[a-z][a-z0-9+.-]*:\/\//iu.test(trimmed)
  ) {
    return false;
  }
  const flavor = pathFlavor(trimmed, cwd);
  const resolvedCwd = flavor.resolve(cwd);
  const resolvedCandidate = flavor.resolve(resolvedCwd, trimmed);
  const normalizedCwd = normalizePath(resolvedCwd, flavor === path.win32);
  const normalizedCandidate = normalizePath(
    resolvedCandidate,
    flavor === path.win32,
  );
  const relative = flavor.relative(normalizedCwd, normalizedCandidate);
  return relative === ".." || relative.startsWith(`..${flavor.sep}`) ||
    flavor.isAbsolute(relative);
}

function pathFlavor(candidate: string, cwd: string): typeof path.win32 {
  if (path.win32.isAbsolute(candidate) || path.win32.isAbsolute(cwd)) {
    return path.win32;
  }
  return path.posix;
}

function normalizePath(value: string, caseInsensitive: boolean): string {
  const normalized = value.replace(/[\\/]+$/u, "");
  return caseInsensitive ? normalized.toLocaleLowerCase("en-US") : normalized;
}
