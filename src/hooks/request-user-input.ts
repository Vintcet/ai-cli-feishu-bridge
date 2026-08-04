import {
  addManagedTerminalMetadata,
  postHook,
  readHookInput,
  writeHookOutput,
} from "./shared.js";

const timeoutMs = Number.parseInt(
  process.env.AI_CLI_FEISHU_INPUT_HTTP_TIMEOUT_MS || "1230000",
  10,
);

try {
  const input = addManagedTerminalMetadata(await readHookInput());
  writeHookOutput(await postHook("/hooks/request-user-input", input, timeoutMs));
} catch (error) {
  console.error(
    `[ai-cli-feishu] Remote question fell back to the local Codex window: ${String(error)}`,
  );
  writeHookOutput({});
}
