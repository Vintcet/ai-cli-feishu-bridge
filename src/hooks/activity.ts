import {
  addManagedTerminalMetadata,
  compactActivityPayload,
  postHook,
  readHookInput,
  writeHookOutput,
} from "./shared.js";

try {
  const input = addManagedTerminalMetadata(await readHookInput());
  writeHookOutput(await postHook("/hooks/activity", compactActivityPayload(input), 2_000));
} catch (error) {
  console.error(`[ai-cli-feishu] Activity update was skipped: ${String(error)}`);
  writeHookOutput({});
}
