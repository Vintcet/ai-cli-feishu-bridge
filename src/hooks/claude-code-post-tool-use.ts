#!/usr/bin/env node
import {
  addClientProcessMetadata,
  addManagedTerminalMetadata,
  compactActivityPayload,
  normalizeClaudeCodePayload,
  postHook,
  readHookInput,
  writeHookOutput,
} from "./shared.js";

try {
  const input = normalizeClaudeCodePayload(await readHookInput());
  const enriched = await addClientProcessMetadata(addManagedTerminalMetadata(input));
  const result = await postHook("/hooks/activity", compactActivityPayload(enriched), 5000);
  writeHookOutput(result);
} catch (error) {
  console.error(`[ai-cli-feishu] Claude Code PostToolUse was skipped: ${String(error)}`);
  writeHookOutput({});
}
