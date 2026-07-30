import assert from "node:assert/strict";
import test from "node:test";

import { isRequestUserInputHookPayload } from "../src/domain.js";

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
