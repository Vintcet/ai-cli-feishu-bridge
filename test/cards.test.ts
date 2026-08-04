import assert from "node:assert/strict";
import test from "node:test";

import {
  buildActivityCard,
  buildStopCard,
  buildStopCards,
  buildUserInputCards,
  buildUserInputQuestionCard,
  buildUserPromptCards,
} from "../src/cards.js";
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
  assert.match(rendered, /\"tag\":\"table\"/);
  assert.match(rendered, /\"display_name\":\"文件\"/);
  assert.match(rendered, /\"display_name\":\"状态\"/);
  assert.match(rendered, /\"column_1\":\"app.ts\"/);
  assert.match(rendered, /\"column_2\":\"完成\"/);
  assert.doesNotMatch(rendered, /文件　｜　状态/);
  assert.match(rendered, /代码 · ts\\nconst ready = true;/);
  assert.match(rendered, /请在发布前检查/);
  assert.match(rendered, /本机报告（K:\/projects\/demo\/report.md:12）/);
  assert.doesNotMatch(rendered, /\[本机报告].*K:\/projects/);
  assert.doesNotMatch(rendered, /引用回复/);
  assert.match(rendered, /下一轮请直接发送消息/);
});

test("Markdown tables use native Feishu table elements and normalize cells", () => {
  const card = buildStopCard(
    session,
    [
      "| **名称** | 说明 | 地址 |",
      "| --- | --- | --- |",
      "| `bridge` | 支持\\|分隔符 | [文档](https://example.com/docs) |",
      "| worker | 第二行 | |",
    ].join("\n"),
  );
  const elements = card.elements as Array<Record<string, unknown>>;
  const table = elements.find((element) => element.tag === "table");

  assert.ok(table);
  assert.equal(table.row_height, "high");
  assert.deepEqual(table.columns, [
    { name: "column_1", display_name: "名称", data_type: "text", width: "auto" },
    { name: "column_2", display_name: "说明", data_type: "text", width: "auto" },
    { name: "column_3", display_name: "地址", data_type: "text", width: "auto" },
  ]);
  assert.deepEqual(table.rows, [
    {
      column_1: "bridge",
      column_2: "支持|分隔符",
      column_3: "文档（https://example.com/docs）",
    },
    { column_1: "worker", column_2: "第二行", column_3: "" },
  ]);
});

test("a card never exceeds Feishu's five native table limit", () => {
  const source = Array.from(
    { length: 6 },
    (_, index) =>
      `| 表 ${index + 1} | 状态 |\n| --- | --- |\n| 内容 ${index + 1} | 完成 |`,
  ).join("\n\n");
  const card = buildStopCard(session, source);
  const elements = card.elements as Array<Record<string, unknown>>;

  assert.equal(elements.filter((element) => element.tag === "table").length, 5);
  assert.match(JSON.stringify(card), /表 6/);
  assert.match(JSON.stringify(card), /内容 6/);
});

test("user input renders one interactive card per question", () => {
  const cards = buildUserInputCards(session, "input-request", [
    {
      header: "发布方式",
      id: "publish",
      question: "如何发布？",
      options: [
        { label: "仅构建", description: "只生成文件" },
        { label: "构建并发布", description: "生成并发布" },
      ],
      custom: false,
    },
    {
      header: "通知范围",
      id: "notify",
      question: "通知谁？",
      options: [
        { label: "团队", description: "通知团队" },
        { label: "负责人", description: "只通知负责人" },
      ],
      custom: false,
    },
  ]);

  assert.equal(cards.length, 2);
  assert.match(JSON.stringify(cards[0]), /发布方式/);
  assert.doesNotMatch(JSON.stringify(cards[0]), /通知范围/);
  assert.match(JSON.stringify(cards[0]), /"action":"input_answer"/);
  assert.match(JSON.stringify(cards[1]), /"questionId":"notify"/);
});

test("multi-choice cards expose toggle and submit actions", () => {
  const question = {
    header: "范围",
    id: "scope",
    question: "选择范围",
    options: [
      { label: "代码", description: "源代码" },
      { label: "测试", description: "自动化测试" },
    ],
    multiple: true,
    custom: false,
  };
  const initial = JSON.stringify(
    buildUserInputQuestionCard(session, "multi-request", question, 0, 1),
  );
  assert.match(initial, /"action":"input_toggle"/);
  assert.match(initial, /"action":"input_submit"/);

  const selected = JSON.stringify(
    buildUserInputQuestionCard(session, "multi-request", question, 0, 1, {
      selectedAnswers: ["代码"],
    }),
  );
  assert.match(selected, /已选 1 项/);
  assert.match(selected, /✓ 代码/);
});

test("external completion cards do not suggest Feishu replies", () => {
  const card = buildStopCard(
    { ...session, managedByAssistant: false },
    "处理完成。",
  );
  const rendered = JSON.stringify(card);

  assert.doesNotMatch(rendered, /引用回复/);
  assert.match(rendered, /不是由 Codex 飞书助手打开.*不能从飞书回复/);
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
