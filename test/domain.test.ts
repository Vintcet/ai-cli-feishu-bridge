import assert from "node:assert/strict";
import test from "node:test";

import {
  isManagedRuntimeName,
  isRequestUserInputHookPayload,
  runtimeDefinition,
  runtimeGroupPrefix,
  runtimeReceivedText,
} from "../src/domain.js";

test("runtime catalog centralizes transport and labels", () => {
  assert.equal(runtimeDefinition().displayName, "Codex");
  assert.equal(runtimeDefinition("claudecode").transport, "managed_terminal");
  assert.equal(runtimeDefinition("opencode").transport, "http_event_stream");
  assert.equal(runtimeGroupPrefix("opencode"), "OpenCode｜");
  assert.equal(runtimeReceivedText("claudecode"), "Claude Code 已接收。");
  assert.equal(isManagedRuntimeName("codex"), true);
  assert.equal(isManagedRuntimeName("claudecode"), true);
  assert.equal(isManagedRuntimeName("opencode"), false);
});

test("request_user_input accepts free-text questions without options", () => {
  const payload = {
    hook_event_name: "PreToolUse",
    session_id: "session-12345678",
    turn_id: "turn-1",
    cwd: "K:\\project",
    tool_name: "request_user_input",
    tool_input: {
      questions: [
        {
          header: "说明",
          id: "details",
          question: "请补充说明",
          options: null,
        },
      ],
    },
  };
  assert.equal(isRequestUserInputHookPayload(payload), true);
  assert.deepEqual(payload.tool_input.questions[0]?.options, []);
});
