import assert from "node:assert/strict";
import test from "node:test";

import { extractLastClaudeAssistantMessage } from "../src/claude-code-transcript.js";
import { normalizeClaudeCodePayload } from "../src/hooks/shared.js";

test("normalizes Claude Code lifecycle and prompt payloads", () => {
  const start = normalizeClaudeCodePayload({
    hook_event_name: "SessionStart",
    session_id: "session-1",
    cwd: "C:/demo",
    source: "unknown",
  }) as Record<string, unknown>;
  assert.equal(start.runtime, "claudecode");
  assert.equal(start.model, "claude-code");
  assert.equal(start.source, "startup");

  const prompt = normalizeClaudeCodePayload({
    hook_event_name: "UserPromptSubmit",
    session_id: "session-1",
    cwd: "C:/demo",
    user_prompt: "hello",
  }) as Record<string, unknown>;
  assert.equal(prompt.prompt, "hello");
});

test("normalizes AskUserQuestion for the bridge input workflow", () => {
  const payload = normalizeClaudeCodePayload({
    hook_event_name: "PreToolUse",
    session_id: "session-1",
    cwd: "C:/demo",
    tool_use_id: "tool-1",
    tool_name: "AskUserQuestion",
    tool_input: {
      questions: [
        {
          header: "发布方式",
          question: "选择发布方式",
          options: [
            { label: "仅构建", description: "只生成文件" },
            { label: "构建并发布", description: "生成并发布" },
          ],
          multiSelect: false,
        },
      ],
    },
  }) as Record<string, unknown>;
  assert.equal(payload.tool_name, "request_user_input");
  assert.match(String(payload.turn_id), /tool-1/);
  const input = payload.tool_input as Record<string, unknown>;
  const questions = input.questions as Array<Record<string, unknown>>;
  assert.equal(questions[0]?.id, "claude_question_1");
  assert.equal(
    (input.claudeCodeQuestionTextById as Record<string, string>).claude_question_1,
    "选择发布方式",
  );
  assert.deepEqual(
    (input.claudeCodeOriginalInput as Record<string, unknown>).questions,
    [
      {
        header: "发布方式",
        question: "选择发布方式",
        options: [
          { label: "仅构建", description: "只生成文件" },
          { label: "构建并发布", description: "生成并发布" },
        ],
        multiSelect: false,
      },
    ],
  );
});

test("extracts the final nested Claude assistant message and stable turn id", () => {
  const transcript = [
    JSON.stringify({ type: "user", message: { role: "user", content: "go" } }),
    JSON.stringify({
      type: "assistant",
      uuid: "assistant-turn-1",
      message: {
        role: "assistant",
        content: [
          { type: "thinking", thinking: "hidden" },
          { type: "text", text: "first paragraph" },
          { type: "text", text: "second paragraph" },
        ],
      },
    }),
    "not-json",
  ].join("\n");

  assert.deepEqual(extractLastClaudeAssistantMessage(transcript), {
    text: "first paragraph\n\nsecond paragraph",
    turnId: "assistant-turn-1",
  });
});
