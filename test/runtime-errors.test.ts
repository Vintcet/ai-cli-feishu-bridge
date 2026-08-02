import assert from "node:assert/strict";
import test from "node:test";

import {
  codexErrorFromMessage,
  isRetryableCodexError,
} from "../src/runtime-errors.js";

test("recognizes high-demand errors as explicit retryable failures", () => {
  const message =
    "We're currently experiencing high demand, which may cause temporary errors.";
  assert.equal(codexErrorFromMessage(message), message);
  assert.equal(isRetryableCodexError(message), true);
  assert.equal(isRetryableCodexError("provider failed", "internal_server_error"), true);
});

test("does not classify explanatory prose as a runtime error", () => {
  assert.equal(
    codexErrorFromMessage(
      "测试已经完成。文档中解释了 500、timeout 和 high demand 分别代表什么。",
    ),
    undefined,
  );
});
