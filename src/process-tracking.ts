import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";

export interface ProcessSnapshot {
  processId: number;
  parentProcessId: number;
  name: string;
  startedAt?: string;
}

export interface ClientProcessMetadata {
  processId: number;
  startedAt?: string;
}

export function findCodexAncestor(
  startProcessId: number,
  snapshots: ProcessSnapshot[],
): ClientProcessMetadata | undefined {
  const byId = new Map(snapshots.map((item) => [item.processId, item]));
  const visited = new Set<number>();
  let currentId = startProcessId;
  for (let depth = 0; depth < 16 && currentId > 0; depth += 1) {
    if (visited.has(currentId)) {
      break;
    }
    visited.add(currentId);
    const current = byId.get(currentId);
    if (!current) {
      break;
    }
    if (/^codex(?:\.exe)?$/i.test(current.name)) {
      return { processId: current.processId, startedAt: current.startedAt };
    }
    currentId = current.parentProcessId;
  }
  return undefined;
}

export function captureCodexAncestor(): ClientProcessMetadata | undefined {
  if (process.platform !== "win32") {
    return undefined;
  }
  const powerShell7 = "C:\\Program Files\\PowerShell\\7\\pwsh.exe";
  const executable = existsSync(powerShell7) ? powerShell7 : "powershell.exe";
  const script = [
    `$current = Get-Process -Id ${process.pid} -ErrorAction Stop`,
    "$items = @()",
    "for ($depth = 0; $depth -lt 16 -and $current; $depth++) {",
    "  $parentId = 0",
    "  try { if ($current.Parent) { $parentId = [int]$current.Parent.Id } } catch {}",
    "  $startedAt = $null",
    "  try { $startedAt = $current.StartTime.ToUniversalTime().ToString('o') } catch {}",
    "  $items += [pscustomobject]@{ processId = [int]$current.Id; parentProcessId = $parentId; name = [string]$current.ProcessName; startedAt = $startedAt }",
    "  if ($parentId -le 0) { break }",
    "  try { $current = Get-Process -Id $parentId -ErrorAction Stop } catch { break }",
    "}",
    "$items | ConvertTo-Json -Compress",
  ].join("\n");
  try {
    const result = spawnSync(
      executable,
      ["-NoProfile", "-NonInteractive", "-Command", script],
      {
        encoding: "utf8",
        timeout: 2_000,
        windowsHide: true,
        maxBuffer: 128 * 1024,
      },
    );
    if (result.status !== 0 || !result.stdout.trim()) {
      return undefined;
    }
    const parsed: unknown = JSON.parse(result.stdout);
    const values = Array.isArray(parsed) ? parsed : [parsed];
    const snapshots = values.flatMap((value): ProcessSnapshot[] => {
      if (!value || typeof value !== "object") {
        return [];
      }
      const item = value as Record<string, unknown>;
      if (
        typeof item.processId !== "number" ||
        !Number.isInteger(item.processId) ||
        typeof item.parentProcessId !== "number" ||
        !Number.isInteger(item.parentProcessId) ||
        typeof item.name !== "string"
      ) {
        return [];
      }
      return [{
        processId: Number(item.processId),
        parentProcessId: Number(item.parentProcessId),
        name: item.name,
        startedAt: typeof item.startedAt === "string" ? item.startedAt : undefined,
      }];
    });
    return findCodexAncestor(process.pid, snapshots);
  } catch {
    return undefined;
  }
}

export function isProcessAlive(processId: number): boolean {
  if (!Number.isSafeInteger(processId) || processId <= 0) {
    return false;
  }
  try {
    process.kill(processId, 0);
    return true;
  } catch (error) {
    return (error as NodeJS.ErrnoException).code === "EPERM";
  }
}
