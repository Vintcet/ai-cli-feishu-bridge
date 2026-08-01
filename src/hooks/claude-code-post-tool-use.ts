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
  const enriched = addClientProcessMetadata(addManagedTerminalMetadata(input));
  const result = await postHook("/hooks/activity", compactActivityPayload(enriched), 5000);
  writeHookOutput(result);
} catch (error) {
  console.error(`[codex-feishu] Claude Code PostToolUse was skipped: ${String(error)}`);
  writeHookOutput({});
}
