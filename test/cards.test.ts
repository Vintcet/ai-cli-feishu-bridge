import assert from "node:assert/strict";
import test from "node:test";

import { buildActivityCard, buildStopCard, buildStopCards, buildUserPromptCards } from "../src/cards.js";
import { splitTextForFeishu } from "../src/feishu-markdown.js";
import type { SessionRecord } from "../src/domain.js";

const session: SessionRecord = {
  sessionId: "019faef0-d0bb-7703-af82-17ee9b45397b",
  shortId: "9b45397b",
  alias: "测试",
  cwd: "K:\\projects\\demo",
  projectName: "demo",
  status: "waiting",
  managedByAssistant: true,
  openedAt: "2026-07-31T08:00:00.000Z",
  lastSeenAt: "2026-07-31T08:05:00.000Z",
};

test("completion cards convert Markdown blocks into Feishu card elements", () => {
  const card = buildStopCard(
    session,
    [
      "# 处理结果",
      "",
      "- 第一项",
      "- [x] 第二项",
      "",
      "| 文件 | 状态 |",
      "| --- | --- |",
      "| app.ts | 完成 |",
      "",
      "```ts",
      "const ready = true;",
      "```",
      "",
      "> 请在发布前检查。",
      "",
      "[本机报告](K:/projects/demo/report.md:12)",
    ].join("\n"),
  );
  const rendered = JSON.stringify(card);

  assert.doesNotMatch(rendered, /# 处理结果/);
  assert.doesNotMatch(rendered, /```/);
  assert.doesNotMatch(rendered, /\| --- \|/);
  assert.match(rendered, /\*\*处理结果\*\*/);
  assert.match(rendered, /• 第一项/);
  assert.match(rendered, /• ☑ 第二项/);
  assert.match(rendered, /文件　｜　状态/);
  assert.match(rendered, /代码 · ts\\nconst ready = true;/);
  assert.match(rendered, /请在发布前检查/);
  assert.match(rendered, /本机报告（K:\/projects\/demo\/report.md:12）/);
  assert.doesNotMatch(rendered, /\[本机报告].*K:\/projects/);
  assert.doesNotMatch(rendered, /引用回复/);
  assert.match(rendered, /下一轮请直接发送消息/);
});

test("external completion cards do not suggest Feishu replies", () => {
  const card = buildStopCard(
    { ...session, managedByAssistant: false },
    "处理完成。",
  );
  const rendered = JSON.stringify(card);

  assert.doesNotMatch(rendered, /引用回复/);
  assert.match(rendered, /外部会话不支持飞书输入/);
});

test("activity detail uses the same Markdown conversion", () => {
  const card = buildActivityCard(
    session,
    [{
      at: "2026-07-31T08:05:00.000Z",
      label: "任务已完成",
      detail: "## 输出\n\n- 测试通过",
    }],
    "2026-07-31T08:00:00.000Z",
  );
  const rendered = JSON.stringify(card);

  assert.doesNotMatch(rendered, /## 输出/);
  assert.match(rendered, /\*\*输出\*\*/);
  assert.match(rendered, /• 测试通过/);
});

test("long completion content is split into complete numbered cards", () => {
  const source = Array.from({ length: 8_000 }, (_, index) => `第${index}段：完整内容。`).join("\n");
  const cards = buildStopCards(session, source);
  assert.ok(cards.length > 1);
  const rendered = cards.map((card) => JSON.stringify(card)).join("\n");
  assert.match(rendered, /（1\/\d+）/);
  assert.match(rendered, /（\d+\/\d+）/);
  assert.doesNotMatch(rendered, /已截断/);
  assert.match(rendered, /第0段/);
  assert.match(rendered, /第7999段/);
});

test("long PC prompts are split without losing content", () => {
  const source = "开头\n" + "内容".repeat(7_000) + "\n结尾";
  const chunks = splitTextForFeishu(source, 2_800);
  assert.ok(chunks.length > 1);
  assert.equal(chunks.join(""), source);
  const cards = buildUserPromptCards(session, source);
  assert.equal(cards.length, chunks.length);
  assert.match(JSON.stringify(cards.at(-1)), /结尾/);
});
