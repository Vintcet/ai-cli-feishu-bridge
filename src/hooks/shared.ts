import { captureCodexAncestor } from "../process-tracking.js";

const defaultBridgeUrl = "http://127.0.0.1:8765";

export async function readHookInput(): Promise<unknown> {
  const chunks: Buffer[] = [];
  for await (const chunk of process.stdin) {
    chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk));
  }
  const text = Buffer.concat(chunks).toString("utf8").trim();
  return text ? JSON.parse(text) : {};
}

export function addManagedTerminalMetadata(input: unknown): unknown {
  if (!input || typeof input !== "object" || Array.isArray(input)) {
    return input;
  }
  const managedTerminalId = process.env.CODEX_FEISHU_MANAGED_TERMINAL_ID?.trim();
  if (!managedTerminalId) {
    return input;
  }
  return {
    ...(input as Record<string, unknown>),
    managed_terminal_id: managedTerminalId,
    managed_terminal_elevated:
      process.env.CODEX_FEISHU_MANAGED_TERMINAL_ELEVATED === "1",
  };
}

export function addClientProcessMetadata(input: unknown): unknown {
  if (
    !input ||
    typeof input !== "object" ||
    Array.isArray(input) ||
    process.env.CODEX_FEISHU_MANAGED_TERMINAL_ID?.trim()
  ) {
    return input;
  }
  const client = captureCodexAncestor();
  if (!client) {
    return input;
  }
  return {
    ...(input as Record<string, unknown>),
    client_process_id: client.processId,
    ...(client.startedAt ? { client_process_started_at: client.startedAt } : {}),
  };
}

export async function postHook(
  pathname: string,
  payload: unknown,
  timeoutMs: number,
): Promise<Record<string, unknown>> {
  const baseUrl = (process.env.CODEX_FEISHU_BRIDGE_URL || defaultBridgeUrl).replace(
    /\/$/,
    "",
  );
  const response = await fetch(`${baseUrl}${pathname}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(payload),
    signal: AbortSignal.timeout(timeoutMs),
  });
  if (!response.ok) {
    return {};
  }
  const value: unknown = await response.json();
  return value && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : {};
}

export function writeHookOutput(value: Record<string, unknown>): void {
  process.stdout.write(JSON.stringify(value));
}
