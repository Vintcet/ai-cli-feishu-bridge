import assert from "node:assert/strict";
import test from "node:test";

import { extractCodexTurnCompletion } from "../src/codex-transcript.js";

test("extracts a structured Codex task_complete error for the requested turn", () => {
  const transcript = [
    JSON.stringify({
      type: "event_msg",
      payload: {
        type: "task_complete",
        turn_id: "turn-old",
        last_agent_message: null,
        error: { message: "older error", codex_error_info: "internal_server_error" },
      },
    }),
    "not-json",
    JSON.stringify({
      type: "event_msg",
      payload: {
        type: "task_complete",
        turn_id: "turn-current",
        last_agent_message: null,
        error: {
          message: "We're currently experiencing high demand, which may cause temporary errors.",
          codex_error_info: "internal_server_error",
        },
      },
    }),
  ].join("\n");

  assert.deepEqual(extractCodexTurnCompletion(transcript, "turn-current"), {
    turnId: "turn-current",
    error: "We're currently experiencing high demand, which may cause temporary errors.",
    errorCode: "internal_server_error",
  });
});

test("does not reuse a structured error from a different Codex turn", () => {
  const transcript = [
    JSON.stringify({
      type: "event_msg",
      payload: {
        type: "task_complete",
        turn_id: "turn-old",
        error: { message: "service unavailable" },
      },
    }),
    JSON.stringify({
      type: "event_msg",
      payload: {
        type: "task_complete",
        turn_id: "turn-current",
        last_agent_message: "Current turn completed normally.",
      },
    }),
  ].join("\n");

  assert.deepEqual(extractCodexTurnCompletion(transcript, "turn-current"), {
    turnId: "turn-current",
    assistantMessage: "Current turn completed normally.",
  });
  assert.equal(extractCodexTurnCompletion(transcript, "turn-missing"), null);
});
