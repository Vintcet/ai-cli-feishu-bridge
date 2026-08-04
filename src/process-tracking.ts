import { execFile } from "node:child_process";
import { existsSync } from "node:fs";
import { promisify } from "node:util";

export interface ProcessSnapshot {
  processId: number;
  parentProcessId: number;
  name: string;
  startedAt?: string;
}

export interface ClientProcessMetadata {
  processId: number;
  startedAt?: string;
  observedAt?: string;
}

interface ProcessMatchCache {
  expiresAt: number;
  lastUsedAt: number;
  processIds: Set<number>;
}

const execFileAsync = promisify(execFile);
const processMatchCaches = new Map<string, ProcessMatchCache>();
const processMatchRefreshes = new Map<string, Promise<void>>();
const processMatchCacheTtlMs = 1_500;
const processMatchCacheRetentionMs = 5 * 60_000;
const provisionalObservationMaxAgeMs = 60_000;

const trackedAssistantProcessPattern = /^(?:codex|claude)(?:\.exe)?$/i;

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
    if (trackedAssistantProcessPattern.test(current.name)) {
      return { processId: current.processId, startedAt: current.startedAt };
    }
    currentId = current.parentProcessId;
  }
  return undefined;
}

export async function captureCodexAncestor(): Promise<ClientProcessMetadata | undefined> {
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
    const result = await execFileAsync(
      executable,
      ["-NoProfile", "-NonInteractive", "-Command", script],
      {
        encoding: "utf8",
        timeout: 2_000,
        windowsHide: true,
        maxBuffer: 128 * 1024,
      },
    );
    if (!result.stdout.trim()) {
      return undefined;
    }
    const snapshots = parseProcessSnapshots(result.stdout);
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
    return process.platform !== "win32" &&
      (error as NodeJS.ErrnoException).code === "EPERM";
  }
}

export function matchTrackedCodexProcessIds(
  clients: ClientProcessMetadata[],
  snapshots: ProcessSnapshot[],
): Set<number> {
  const snapshotById = new Map(
    snapshots.map((snapshot) => [snapshot.processId, snapshot]),
  );
  const matches = new Set<number>();
  for (const client of clients) {
    const snapshot = snapshotById.get(client.processId);
    if (!snapshot || !trackedAssistantProcessPattern.test(snapshot.name)) {
      continue;
    }
    if (client.startedAt) {
      const expected = Date.parse(client.startedAt);
      const actual = snapshot.startedAt ? Date.parse(snapshot.startedAt) : Number.NaN;
      if (
        !Number.isFinite(expected) ||
        !Number.isFinite(actual) ||
        Math.abs(actual - expected) > 1_000
      ) {
        continue;
      }
    }
    matches.add(client.processId);
  }
  return matches;
}

export function captureLiveTrackedCodexProcessIds(
  clients: ClientProcessMetadata[],
): Set<number> {
  const uniqueClients = [
    ...new Map(
      clients
        .filter(
          (client) => Number.isSafeInteger(client.processId) && client.processId > 0,
        )
        .map((client) => [client.processId, client]),
    ).values(),
  ].sort((left, right) => left.processId - right.processId);
  if (uniqueClients.length === 0) {
    return new Set();
  }
  if (process.platform !== "win32") {
    return new Set(
      uniqueClients
        .filter((client) => isProcessAlive(client.processId))
        .map((client) => client.processId),
    );
  }

  const cacheKey = uniqueClients
    .map((client) => `${client.processId}:${client.startedAt ?? ""}`)
    .join("|");
  const now = Date.now();
  for (const [key, cached] of processMatchCaches) {
    if (
      key !== cacheKey &&
      cached.lastUsedAt + processMatchCacheRetentionMs <= now &&
      !processMatchRefreshes.has(key)
    ) {
      processMatchCaches.delete(key);
    }
  }
  const cached = processMatchCaches.get(cacheKey);
  if (cached) {
    cached.lastUsedAt = now;
    if (cached.expiresAt <= now) {
      scheduleProcessMatchRefresh(cacheKey, uniqueClients);
    }
    return new Set(cached.processIds);
  }

  const fallbackMatches = provisionalTrackedAssistantProcessIds(
    uniqueClients,
    now,
  );
  processMatchCaches.set(cacheKey, {
    expiresAt: now + processMatchCacheTtlMs,
    lastUsedAt: now,
    processIds: new Set(fallbackMatches),
  });
  scheduleProcessMatchRefresh(cacheKey, uniqueClients);
  return fallbackMatches;
}

function scheduleProcessMatchRefresh(
  cacheKey: string,
  clients: ClientProcessMetadata[],
): void {
  if (processMatchRefreshes.has(cacheKey)) {
    return;
  }
  const refresh = inspectTrackedCodexProcessIds(clients)
    .then((processIds) => {
      const previous = processMatchCaches.get(cacheKey);
      processMatchCaches.set(cacheKey, {
        expiresAt: Date.now() + processMatchCacheTtlMs,
        lastUsedAt: previous?.lastUsedAt ?? Date.now(),
        processIds,
      });
    })
    .catch(() => undefined)
    .finally(() => {
      if (processMatchRefreshes.get(cacheKey) === refresh) {
        processMatchRefreshes.delete(cacheKey);
      }
    });
  processMatchRefreshes.set(cacheKey, refresh);
}

export function provisionalTrackedAssistantProcessIds(
  clients: ClientProcessMetadata[],
  now = Date.now(),
  processAlive: (processId: number) => boolean = isProcessAlive,
): Set<number> {
  return new Set(
    clients
      .filter((client) => {
        if (!processAlive(client.processId)) {
          return false;
        }
        const observedAt = Date.parse(client.observedAt ?? client.startedAt ?? "");
        return Number.isFinite(observedAt) &&
          observedAt <= now + 1_000 &&
          now - observedAt <= provisionalObservationMaxAgeMs;
      })
      .map((client) => client.processId),
  );
}

async function inspectTrackedCodexProcessIds(
  clients: ClientProcessMetadata[],
): Promise<Set<number>> {
  const powerShell7 = "C:\\Program Files\\PowerShell\\7\\pwsh.exe";
  const executable = existsSync(powerShell7) ? powerShell7 : "powershell.exe";
  const processIds = clients.map((client) => client.processId).join(",");
  const script = [
    `$ids = @(${processIds})`,
    "$items = foreach ($processId in $ids) {",
    "  try {",
    "    $item = Get-Process -Id $processId -ErrorAction Stop",
    "    $startedAt = $null",
    "    try { $startedAt = $item.StartTime.ToUniversalTime().ToString('o') } catch {}",
    "    [pscustomobject]@{ processId = [int]$item.Id; parentProcessId = 0; name = [string]$item.ProcessName; startedAt = $startedAt }",
    "  } catch {}",
    "}",
    "@($items) | ConvertTo-Json -Compress",
  ].join("\n");

  const result = await execFileAsync(
    executable,
    ["-NoProfile", "-NonInteractive", "-Command", script],
    {
      encoding: "utf8",
      timeout: 2_000,
      windowsHide: true,
      maxBuffer: 128 * 1024,
    },
  );
  if (!result.stdout.trim()) {
    throw new Error("Could not inspect tracked processes.");
  }
  return matchTrackedCodexProcessIds(clients, parseProcessSnapshots(result.stdout));
}

function parseProcessSnapshots(text: string): ProcessSnapshot[] {
  const parsed: unknown = JSON.parse(text);
  const values = Array.isArray(parsed) ? parsed : [parsed];
  return values.flatMap((value): ProcessSnapshot[] => {
    if (!value || typeof value !== "object") {
      return [];
    }
    const item = value as Record<string, unknown>;
    if (
      typeof item.processId !== "number" ||
      !Number.isSafeInteger(item.processId) ||
      typeof item.parentProcessId !== "number" ||
      !Number.isSafeInteger(item.parentProcessId) ||
      typeof item.name !== "string"
    ) {
      return [];
    }
    return [{
      processId: item.processId,
      parentProcessId: item.parentProcessId,
      name: item.name,
      startedAt: typeof item.startedAt === "string" ? item.startedAt : undefined,
    }];
  });
}
