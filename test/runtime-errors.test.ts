import assert from "node:assert/strict";
import test from "node:test";

import {
  codexErrorFromMessage,
  isRetryableRuntimeError,
} from "../src/runtime-errors.js";

test("recognizes high-demand errors as explicit retryable failures", () => {
  const message =
    "We're currently experiencing high demand, which may cause temporary errors.";
  assert.equal(codexErrorFromMessage(message), message);
  assert.equal(isRetryableRuntimeError(message), true);
  assert.equal(isRetryableRuntimeError("provider failed", "internal_server_error"), true);
});

test("recognizes common 502 gateway error formats", () => {
  const messages = [
    "API Error: 502 Bad Gateway",
    "Request failed with status code 502",
    "Bad Gateway (502)",
    "HTTP/1.1 502 Bad Gateway",
  ];
  for (const message of messages) {
    assert.equal(codexErrorFromMessage(message), message);
    assert.equal(isRetryableRuntimeError(message), true);
  }
  assert.equal(isRetryableRuntimeError("provider failed", "bad_gateway_502"), true);
});

test("recognizes retry exhaustion after a Codex terminal status marker", () => {
  const message = "■ exceeded retry limit, last status: 429 Too Many Requests";
  assert.equal(codexErrorFromMessage(message), message);
  assert.equal(isRetryableRuntimeError(message), true);
});

test("does not classify explanatory prose as a runtime error", () => {
  assert.equal(
    codexErrorFromMessage(
      "测试已经完成。文档中解释了 500、timeout 和 high demand 分别代表什么。",
    ),
    undefined,
  );
});
