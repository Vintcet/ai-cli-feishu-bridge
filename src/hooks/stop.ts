import {
  addManagedTerminalMetadata,
  postHook,
  readHookInput,
  writeHookOutput,
} from "./shared.js";

try {
  const input = addManagedTerminalMetadata(await readHookInput());
  writeHookOutput(await postHook("/hooks/stop", input, 10_000));
} catch (error) {
  console.error(`[ai-cli-feishu] Stop notification was skipped: ${String(error)}`);
  writeHookOutput({});
}
