#!/usr/bin/env node
import {
  addClientProcessMetadata,
  addManagedTerminalMetadata,
  normalizeClaudeCodePayload,
  postHook,
  readHookInput,
  writeHookOutput,
} from "./shared.js";

try {
  const input = normalizeClaudeCodePayload(await readHookInput());
  const enriched = await addClientProcessMetadata(addManagedTerminalMetadata(input));
  const result = await postHook("/hooks/stop", enriched, 20000);
  writeHookOutput(result);
} catch (error) {
  console.error(`[codex-feishu] Claude Code Stop was skipped: ${String(error)}`);
  writeHookOutput({});
}
