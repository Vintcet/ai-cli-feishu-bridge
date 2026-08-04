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
  const isQuestion = Boolean(
    enriched &&
      typeof enriched === "object" &&
      !Array.isArray(enriched) &&
      (enriched as Record<string, unknown>).tool_name === "request_user_input",
  );
  const result = isQuestion
    ? await postHook("/hooks/request-user-input", enriched, 1_500_000)
    : await postHook("/hooks/activity", compactActivityPayload(enriched), 5000);
  writeHookOutput(result);
} catch (error) {
  console.error(`[ai-cli-feishu] Claude Code PreToolUse was skipped: ${String(error)}`);
  writeHookOutput({});
}
