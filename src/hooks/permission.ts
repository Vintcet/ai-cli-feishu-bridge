import {
  addManagedTerminalMetadata,
  postHook,
  readHookInput,
  writeHookOutput,
} from "./shared.js";

const timeoutMs = Number.parseInt(
  process.env.AI_CLI_FEISHU_PERMISSION_HTTP_TIMEOUT_MS || "1230000",
  10,
);

try {
  const input = addManagedTerminalMetadata(await readHookInput());
  writeHookOutput(await postHook("/hooks/permission", input, timeoutMs));
} catch (error) {
  console.error(`[ai-cli-feishu] Permission hook fell back to local approval: ${String(error)}`);
  writeHookOutput({});
}
