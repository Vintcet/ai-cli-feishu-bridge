import {
  addManagedTerminalMetadata,
  postHook,
  readHookInput,
  writeHookOutput,
} from "./shared.js";

try {
  const input = addManagedTerminalMetadata(await readHookInput());
  writeHookOutput(await postHook("/hooks/activity", compactActivityPayload(input), 2_000));
} catch (error) {
  console.error(`[codex-feishu] Activity update was skipped: ${String(error)}`);
  writeHookOutput({});
}

function compactActivityPayload(value: unknown): unknown {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return value;
  }
  const item = value as Record<string, unknown>;
  return {
    hook_event_name: item.hook_event_name,
    session_id: item.session_id,
    turn_id: item.turn_id,
    cwd: item.cwd,
    model: item.model,
    prompt: item.prompt,
    tool_name: item.tool_name,
    tool_preview: compactPreview(item.tool_input),
    tool_response_preview: compactPreview(
      item.tool_response ?? item.tool_result ?? item.tool_output,
    ),
    managed_terminal_id: item.managed_terminal_id,
    managed_terminal_elevated: item.managed_terminal_elevated,
  };
}

function compactPreview(value: unknown): string | undefined {
  if (value === undefined) return undefined;
  let text: string;
  try {
    text = JSON.stringify(value) ?? String(value);
  } catch {
    text = String(value);
  }
  return text.length <= 1_200 ? text : `${text.slice(0, 1_180)}…（已截断）`;
}
