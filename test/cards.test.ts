import assert from "node:assert/strict";
import test from "node:test";

import {
  buildActivityCard,
  buildApprovalCard,
  buildErrorCards,
  buildRuntimeProjectFormCard,
  buildRuntimeSelectionCard,
  buildStopCard,
  buildStopCards,
  buildUserInputCards,
  buildUserInputQuestionCard,
  buildUserPromptCards,
} from "../src/cards.js";
import { splitTextForFeishu } from "../src/feishu-markdown.js";
import type { ApprovalRecord, SessionRecord } from "../src/domain.js";

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

test("/新建 uses one card with three runtime choices", () => {
  const card = buildRuntimeSelectionCard("K:\\workspace", {
    flowId: "runtime-flow",
    sourceMessageId: "slash-new-message",
    chatId: "chat-owner",
  });
  const actions = findCardRecords(
    card,
    (record) => record.action === "runtime_new_select",
  );

  assert.equal(actions.length, 3);
  assert.deepEqual(
    actions.map((action) => action.runtime),
    ["codex", "claudecode", "opencode"],
  );
  assert.ok(actions.every((action) => action.flowId === "runtime-flow"));
  const runtimeButtons = findCardRecords(card, (record) => record.tag === "button");
  assert.ok(runtimeButtons.every((button) => {
    const behaviors = button.behaviors;
    return Array.isArray(behaviors) &&
      behaviors.some((behavior) =>
        behavior &&
        typeof behavior === "object" &&
        (behavior as Record<string, unknown>).type === "callback" &&
        (behavior as Record<string, unknown>).value &&
        typeof (behavior as Record<string, unknown>).value === "object"
      );
  }));
  assert.equal(
    findCardRecords(card, (record) => record.tag === "button").length,
    3,
  );
  const rendered = JSON.stringify(card);
  assert.match(rendered, /三个运行环境都是 \/新建 的二级选项/);
  assert.doesNotMatch(rendered, /\/(?:codex|claude|opencode)/i);
});

test("runtime project cards submit a required project name form", () => {
  const context = {
    flowId: "runtime-flow",
    sourceMessageId: "slash-new-message",
    chatId: "chat-owner",
  };

  for (const runtime of ["codex", "claudecode", "opencode"] as const) {
    const card = buildRuntimeProjectFormCard(runtime, "K:\\workspace", context);
    const forms = findCardRecords(card, (record) => record.tag === "form");
    const inputs = findCardRecords(
      card,
      (record) => record.tag === "input" && record.name === "project_name",
    );
    const submitActions = findCardRecords(
      card,
      (record) => record.action === "runtime_new_submit",
    );
    const cancelActions = findCardRecords(
      card,
      (record) => record.action === "runtime_new_cancel",
    );

    assert.equal(forms.length, 1);
    assert.equal(inputs.length, 1);
    assert.equal(inputs[0]?.required, true);
    assert.equal(submitActions.length, 1);
    assert.equal(submitActions[0]?.runtime, runtime);
    assert.equal(cancelActions.length, 1);
    assert.equal(cancelActions[0]?.runtime, runtime);

    const submitButtons = findCardRecords(
      card,
      (record) =>
        record.tag === "button" &&
        record.action_type === "form_submit" &&
        record.name === "runtime_new_submit",
    );
    assert.equal(submitButtons.length, 1);
    assert.equal(submitButtons[0]?.complex_interaction, true);
    for (const button of [submitButtons[0], ...findCardRecords(card, (record) =>
      record.tag === "button" && record.name === "runtime_new_cancel"
    )]) {
      const behaviors = button?.behaviors;
      assert.ok(Array.isArray(behaviors));
      assert.equal((behaviors[0] as Record<string, unknown>)?.type, "callback");
    }
  }
});

test("approval cards keep PC approval behind an explicit transfer action", () => {
  const approval: ApprovalRecord = {
    requestId: "approval-request",
    sessionId: session.sessionId,
    turnId: "turn-1",
    cwd: session.cwd,
    toolName: "Bash",
    toolPreview: '{"command":"npm test"}',
    createdAt: "2026-08-04T00:00:00.000Z",
    expiresAt: "2026-08-04T00:20:00.000Z",
    status: "pending",
    messageIds: [],
    requiresManualApproval: true,
    desktopApprovalRequested: false,
    riskLevel: "low",
  };
  const rendered = JSON.stringify(buildApprovalCard(session, approval));

  assert.match(rendered, /批准一次/);
  assert.match(rendered, /拒绝/);
  assert.match(rendered, /转回 PC 审批/);
  assert.match(rendered, /"action":"approval_desktop"/);
  const buttons = findCardRecords(buildApprovalCard(session, approval), (record) => record.tag === "button");
  assert.equal(buttons.length, 3);
  assert.ok(buttons.every((button) =>
    Array.isArray(button.behaviors) &&
    (button.behaviors as Array<Record<string, unknown>>).some((behavior) => behavior.type === "callback")
  ));
});

test("retryable error cards expose a stop action only while retrying", () => {
  const scheduled = JSON.stringify(
    buildErrorCards(session, "API Error: 502 Bad Gateway", {
      cycleId: "retry-cycle-1",
      state: "scheduled",
    }),
  );
  assert.match(scheduled, /停止自动重试/);
  assert.match(scheduled, /"action":"retry_stop"/);
  assert.match(scheduled, /"retryCycleId":"retry-cycle-1"/);

  const stopped = JSON.stringify(
    buildErrorCards(session, "API Error: 502 Bad Gateway", {
      cycleId: "retry-cycle-1",
      state: "stopped",
    }),
  );
  assert.doesNotMatch(stopped, /"action":"retry_stop"/);
  assert.match(stopped, /已停止自动重试/);
});

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
  assert.match(rendered, /不是由 AI CLI 飞书助手打开.*不能从飞书回复/);
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

function findCardRecords(
  value: unknown,
  predicate: (record: Record<string, unknown>) => boolean,
): Array<Record<string, unknown>> {
  if (!value || typeof value !== "object") {
    return [];
  }
  if (Array.isArray(value)) {
    return value.flatMap((item) => findCardRecords(item, predicate));
  }
  const record = value as Record<string, unknown>;
  return [
    ...(predicate(record) ? [record] : []),
    ...Object.values(record).flatMap((child) =>
      findCardRecords(child, predicate)
    ),
  ];
}
