import { addManagedTerminalMetadata, postHook, readHookInput } from "./shared.js";

try {
  const input = addManagedTerminalMetadata(await readHookInput());
  await postHook("/hooks/session-end", input, 1_500);
} catch (error) {
  console.error(`[codex-feishu] Session end registration was skipped: ${String(error)}`);
}
