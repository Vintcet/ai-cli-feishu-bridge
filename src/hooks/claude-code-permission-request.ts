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
  const enriched = addClientProcessMetadata(addManagedTerminalMetadata(input));
  const isQuestion = Boolean(
    enriched &&
      typeof enriched === "object" &&
      !Array.isArray(enriched) &&
      (enriched as Record<string, unknown>).tool_name === "AskUserQuestion",
  );
  const result = isQuestion
    ? {}
    : await postHook("/hooks/permission", enriched, 1_500_000);
  writeHookOutput(result);
} catch (error) {
  console.error(`[codex-feishu] Claude Code PermissionRequest was skipped: ${String(error)}`);
  writeHookOutput({});
}
