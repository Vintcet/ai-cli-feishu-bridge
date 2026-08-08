using System.Text.Encodings.Web;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;

namespace AiCliFeishu.Bridge.FeishuAdapter.Tests;

[TestClass]
public sealed class FeishuCardRendererTests
{
    [TestMethod]
    public void CompletionCardConvertsMarkdownAndAddsManagedFooter()
    {
        var renderer = new FeishuCardRenderer();
        var card = renderer.RuntimeCompletion(
            Session(managed: true),
            string.Join('\n',
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
            ]));

        var json = Json(card.Single());
        Assert.IsFalse(json.Contains("# 处理结果", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("```", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("| --- |", StringComparison.Ordinal));
        StringAssert.Contains(json, "**处理结果**");
        StringAssert.Contains(json, "• 第一项");
        StringAssert.Contains(json, "• ☑ 第二项");
        StringAssert.Contains(json, "\"tag\":\"table\"");
        StringAssert.Contains(json, "\"display_name\":\"文件\"");
        StringAssert.Contains(json, "代码 · ts");
        StringAssert.Contains(json, "请在发布前检查");
        StringAssert.Contains(json, "本机报告（K:/projects/demo/report.md:12）");
        StringAssert.Contains(json, "下一轮请直接发送消息");
    }

    [TestMethod]
    public void CompletionCardNormalizesTableCellsAndLimitsNativeTables()
    {
        var renderer = new FeishuCardRenderer();
        var table = renderer.RuntimeCompletion(
            Session(managed: true),
            string.Join("\n",
            [
                "| **名称** | 说明 | 地址 |",
                "| --- | --- | --- |",
                "| bridge | 支持\\|分隔符 | [文档](https://example.com/docs) |",
                "| worker | 第二行 | |",
            ]))
            .Single();
        var tableJson = Json(table);

        StringAssert.Contains(tableJson, "\"display_name\":\"名称\"");
        StringAssert.Contains(tableJson, "\"column_1\":\"bridge\"");
        StringAssert.Contains(tableJson, "\"column_2\":\"支持|分隔符\"");
        StringAssert.Contains(tableJson, "\"column_3\":\"文档（https://example.com/docs）\"");

        var manyTables = string.Join(
            "\n\n",
            Enumerable.Range(1, 6).Select(index =>
                $"| 表 {index} | 状态 |\n| --- | --- |\n| 内容 {index} | 完成 |"));
        var limited = Json(renderer.RuntimeCompletion(
            Session(managed: true),
            manyTables)
            .Single());

        Assert.AreEqual(
            5,
            limited.Split("\"tag\":\"table\"", StringSplitOptions.None).Length - 1);
        StringAssert.Contains(limited, "表 6");
        StringAssert.Contains(limited, "内容 6");
    }

    [TestMethod]
    public void ExternalCompletionCardDoesNotSuggestFeishuReply()
    {
        var renderer = new FeishuCardRenderer();
        var card = renderer.RuntimeCompletion(Session(managed: false), "处理完成。")
            .Single();
        var json = Json(card);

        Assert.IsFalse(json.Contains("引用回复", StringComparison.Ordinal));
        StringAssert.Contains(json, "不是由 AI CLI 飞书助手打开");
    }

    [TestMethod]
    public void LongCompletionIsSplitWithoutDroppingContent()
    {
        var renderer = new FeishuCardRenderer();
        var source = string.Concat(
            Enumerable.Range(0, 8_000).Select(index => $"第{index}段：完整内容。\n"));
        var cards = renderer.RuntimeCompletion(Session(managed: true), source);

        Assert.IsTrue(cards.Count > 1);
        var json = string.Join('\n', cards.Select(Json));
        StringAssert.Contains(json, "（1/");
        StringAssert.Contains(json, "第0段");
        StringAssert.Contains(json, "第7999段");
        Assert.IsFalse(json.Contains("已截断", StringComparison.Ordinal));
    }

    [TestMethod]
    public void QuestionCompletionUsesWaitingTitleAndRedactsSecrets()
    {
        var renderer = new FeishuCardRenderer();
        var card = renderer.RuntimeCompletion(
                Session(managed: true),
                "请确认是否继续？\nAPI_TOKEN=super-secret")
            .Single();
        var json = Json(card);

        StringAssert.Contains(json, "Codex 等待你回复");
        StringAssert.Contains(json, "API_TOKEN=[已隐藏]");
        Assert.IsFalse(json.Contains("super-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ActivityCardRendersProgressDetailsAndRedactsSecrets()
    {
        var renderer = new FeishuCardRenderer();
        var card = renderer.RuntimeActivity(
            Session(managed: false),
            [
                new(
                    "2026-08-06T01:02:03Z",
                    "正在调用命令行",
                    "{\"command\":\"git status\",\"API_TOKEN\":\"secret-value\"}"),
                new("2026-08-06T01:02:04Z", "命令行 已完成", "输出正常"),
            ],
            "2026-08-06T01:02:00Z");
        var json = Json(card);

        StringAssert.Contains(json, "Codex 正在处理");
        StringAssert.Contains(json, "会话：");
        StringAssert.Contains(json, "开始：");
        StringAssert.Contains(json, "目录：");
        StringAssert.Contains(json, "正在调用命令行");
        StringAssert.Contains(json, "输出正常");
        StringAssert.Contains(json, "API_TOKEN");
        Assert.IsFalse(json.Contains("secret-value", StringComparison.Ordinal));
        StringAssert.Contains(json, "同一轮只保留一张进度卡");
    }

    [TestMethod]
    public void CompletedActivityCardIsGreenAndDropsActiveFooter()
    {
        var renderer = new FeishuCardRenderer();
        var events = Enumerable.Range(1, 8)
            .Select(index => new FeishuActivityEventView(
                $"2026-08-06T01:02:{index:00}Z",
                $"活动 {index}"))
            .ToArray();
        var card = renderer.RuntimeActivity(
            Session(managed: true),
            events,
            "2026-08-06T01:02:00Z",
            completed: true);
        var json = Json(card);

        StringAssert.Contains(json, "Codex 本轮处理完成");
        StringAssert.Contains(json, "\"template\":\"green\"");
        StringAssert.Contains(json, "活动 8");
        Assert.IsFalse(json.Contains("活动 1", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("同一轮只保留一张进度卡", StringComparison.Ordinal));
    }

    private static FeishuSessionView Session(bool managed) => new(
        "session-renderer-1",
        "codex",
        "renderer-test",
        "K:/repo",
        managed);

    private static string Json(FeishuCardView card) => card.Content.ToJsonString(
        new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
}
