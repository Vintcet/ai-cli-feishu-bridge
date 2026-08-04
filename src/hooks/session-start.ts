import {
  addClientProcessMetadata,
  addManagedTerminalMetadata,
  postHook,
  readHookInput,
  writeHookOutput,
} from "./shared.js";

try {
  const input = await addClientProcessMetadata(
    addManagedTerminalMetadata(await readHookInput()),
  );
  writeHookOutput(await postHook("/hooks/session-start", input, 5_000));
} catch (error) {
  console.error(`[codex-feishu] Session start registration was skipped: ${String(error)}`);
  writeHookOutput({});
}
