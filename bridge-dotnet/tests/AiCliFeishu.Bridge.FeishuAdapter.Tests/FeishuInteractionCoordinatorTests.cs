using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.FeishuAdapter.Tests;

[TestClass]
public sealed class FeishuInteractionCoordinatorTests
{
    private static readonly DateTimeOffset Origin =
        new(2026, 8, 6, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task LocalApprovalResolutionPatchesEveryCardAndMakesFeishuDuplicateNoOp()
    {
        var gateway = new RecordingFeishuGateway();
        var coordinator = Coordinator(gateway);
        var state = ApprovalStateMachine.Create(
            ApprovalRegistryState.Empty,
            new ApprovalState(
                "approval-1",
                "session-1",
                ApprovalStatuses.Pending,
                Origin,
                Origin.AddMinutes(10),
                ["card-owner", "card-group"]));

        var local = ApprovalStateMachine.ResolveExternally(
            state,
            "approval-1",
            ApprovalResolutions.Allow,
            Origin.AddMinutes(1));
        await coordinator.SynchronizeApprovalAsync(
            local.State.Requests["approval-1"],
            Session(),
            Approval());
        var feishuDuplicate = await coordinator.ResolveApprovalAsync(
            local.State,
            "approval-1",
            ApprovalResolutions.Deny,
            Origin.AddMinutes(2),
            Session(),
            Approval());

        Assert.IsTrue(local.Value);
        Assert.IsFalse(feishuDuplicate.Value);
        Assert.AreEqual(2, gateway.Patches.Count);
        CollectionAssert.AreEquivalent(
            new[] { "card-owner", "card-group" },
            gateway.Patches.Select(item => item.MessageId).ToArray());
        foreach (var patch in gateway.Patches)
        {
            var title = patch.Card.Content["header"]!["title"]!["content"]!.GetValue<string>();
            var content = patch.Card.Content["elements"]![0]!["text"]!["content"]!.GetValue<string>();
            Assert.AreEqual("Codex 审批已处理", title);
            StringAssert.Contains(content, "已批准");
            var json = patch.Card.Content.ToJsonString();
            Assert.IsFalse(json.Contains("approval_deny", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task FeishuApprovalResolutionPatchesOnceAndRejectsSecondClick()
    {
        var gateway = new RecordingFeishuGateway();
        var coordinator = Coordinator(gateway);
        var state = ApprovalStateMachine.Create(
            ApprovalRegistryState.Empty,
            new ApprovalState(
                "approval-1",
                "session-1",
                ApprovalStatuses.Pending,
                Origin,
                Origin.AddMinutes(10),
                ["card-1"]));

        var first = await coordinator.ResolveApprovalAsync(
            state,
            "approval-1",
            ApprovalResolutions.Deny,
            Origin.AddMinutes(1),
            Session(),
            Approval());
        var duplicate = await coordinator.ResolveApprovalAsync(
            first.State,
            "approval-1",
            ApprovalResolutions.Allow,
            Origin.AddMinutes(2),
            Session(),
            Approval());

        Assert.IsTrue(first.Value);
        Assert.IsFalse(duplicate.Value);
        Assert.AreEqual(1, gateway.Patches.Count);
        Assert.AreEqual(ApprovalResolutions.Deny, first.State.Requests["approval-1"].Resolution);
    }

    [TestMethod]
    public async Task ReplayingSameResolvedApprovalDoesNotPatchTwice()
    {
        var gateway = new RecordingFeishuGateway();
        var coordinator = Coordinator(gateway);
        var resolved = ApprovalStateMachine.ResolveExternally(
            ApprovalStateMachine.Create(
                ApprovalRegistryState.Empty,
                new ApprovalState(
                    "approval-1",
                    "session-1",
                    ApprovalStatuses.Pending,
                    Origin,
                    Origin.AddMinutes(10),
                    ["card-1"])),
            "approval-1",
            ApprovalResolutions.Local,
            Origin.AddMinutes(1)).State.Requests["approval-1"];

        await coordinator.SynchronizeApprovalAsync(resolved, Session(), Approval());
        await coordinator.SynchronizeApprovalAsync(resolved, Session(), Approval());

        Assert.AreEqual(1, gateway.Patches.Count);
    }

    [TestMethod]
    public async Task DeferredApprovalUsesDesktopCardWithoutResolvingPendingState()
    {
        var gateway = new RecordingFeishuGateway();
        var coordinator = Coordinator(gateway);
        var pending = new ApprovalState(
            "approval-1",
            "session-1",
            ApprovalStatuses.Pending,
            Origin,
            Origin.AddMinutes(10),
            ["card-1"]);

        await coordinator.SynchronizeDeferredApprovalAsync(
            pending,
            Session(),
            Approval());
        await coordinator.SynchronizeDeferredApprovalAsync(
            pending,
            Session(),
            Approval());

        Assert.AreEqual(ApprovalStatuses.Pending, pending.Status);
        Assert.AreEqual(1, gateway.Patches.Count);
        var title = gateway.Patches[0].Card.Content["header"]!["title"]![
            "content"]!.GetValue<string>();
        var json = gateway.Patches[0].Card.Content.ToJsonString();
        StringAssert.Contains(title, "已转回 PC 审批");
        Assert.IsFalse(json.Contains("approval_allow", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task FailedPatchCanBeRetried()
    {
        var gateway = new RecordingFeishuGateway { FailPatchCount = 1 };
        var coordinator = Coordinator(gateway);
        var resolved = ApprovalStateMachine.ResolveExternally(
            ApprovalStateMachine.Create(
                ApprovalRegistryState.Empty,
                new ApprovalState(
                    "approval-1",
                    "session-1",
                    ApprovalStatuses.Pending,
                    Origin,
                    Origin.AddMinutes(10),
                    ["card-1"])),
            "approval-1",
            ApprovalResolutions.Allow,
            Origin.AddMinutes(1)).State.Requests["approval-1"];

        await Assert.ThrowsExceptionAsync<HttpRequestException>(() =>
            coordinator.SynchronizeApprovalAsync(resolved, Session(), Approval()));
        await coordinator.SynchronizeApprovalAsync(resolved, Session(), Approval());

        Assert.AreEqual(1, gateway.Patches.Count);
    }

    [TestMethod]
    public async Task LocalInputResolutionPatchesAllQuestionCardsWithoutButtons()
    {
        var gateway = new RecordingFeishuGateway();
        var coordinator = Coordinator(gateway);
        var questions = Questions();
        var state = InputStateMachine.Create(
            InputRegistryState.Empty,
            new InputRequestState(
                "input-1",
                "session-1",
                InputRequestStatuses.Pending,
                Origin,
                Origin.AddMinutes(10),
                questions.Select(item => new InputQuestionState(
                    item.Id,
                    item.Multiple,
                    item.AllowsCustom,
                    item.Options)).ToArray(),
                new Dictionary<string, IReadOnlyList<string>>()));

        var local = await coordinator.ResolveInputLocallyAsync(
            state,
            "input-1",
            Origin.AddMinutes(1),
            Session(),
            questions,
            [new("card-q1", "q1", 0), new("card-q2", "q2", 1)]);

        Assert.IsTrue(local.Value);
        Assert.AreEqual(InputRequestStatuses.Local, local.State.Requests["input-1"].Status);
        Assert.AreEqual(2, gateway.Patches.Count);
        foreach (var patch in gateway.Patches)
        {
            var title = patch.Card.Content["header"]!["title"]!["content"]!.GetValue<string>();
            var content = patch.Card.Content["elements"]![0]!["text"]!["content"]!.GetValue<string>();
            StringAssert.Contains(title, "已处理");
            StringAssert.Contains(content, "已转回电脑端");
            var json = patch.Card.Content.ToJsonString();
            Assert.IsFalse(json.Contains("input_answer", StringComparison.Ordinal));
            Assert.IsFalse(json.Contains("input_local", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task RecordedAndResetInputCardsPreserveQuestionAndSelectionScope()
    {
        var gateway = new RecordingFeishuGateway();
        var coordinator = Coordinator(gateway);
        var questions = Questions();
        var request = new InputRequestState(
            "input-1",
            "session-1",
            InputRequestStatuses.Pending,
            Origin,
            Origin.AddMinutes(10),
            questions.Select(item => new InputQuestionState(
                item.Id,
                item.Multiple,
                item.AllowsCustom,
                item.Options)).ToArray(),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["q1"] = ["Codex"],
            });
        var targets = new[]
        {
            new FeishuInputCardTarget("card-q1", "q1", 0, "chat-1"),
            new FeishuInputCardTarget("card-q2", "q2", 1, "chat-1"),
        };

        await coordinator.SynchronizeRecordedInputAsync(
            request,
            Session(),
            questions,
            targets,
            "q1",
            "event-1");
        await coordinator.SynchronizePendingInputAsync(
            request with
            {
                Answers = new Dictionary<string, IReadOnlyList<string>>(
                    StringComparer.Ordinal),
            },
            Session(),
            questions,
            targets,
            new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(
                StringComparer.Ordinal)
            {
                ["chat-1"] = new Dictionary<string, IReadOnlyList<string>>(
                    StringComparer.Ordinal)
                {
                    ["q2"] = ["快速"],
                },
            },
            "event-2");

        Assert.AreEqual(3, gateway.Patches.Count);
        StringAssert.Contains(CardText(gateway.Patches[0].Card), "还剩 1 个问题");
        var reset = gateway.Patches.Single(item => item.MessageId == "card-q2").Card;
        StringAssert.Contains(CardText(reset), "✓ 快速");
        var values = ActionRows(reset)
            .SelectMany(row => row["actions"]!.AsArray())
            .Select(button => ActionValue(button!))
            .ToArray();
        Assert.IsTrue(values.All(value =>
            value["selectionKey"]!.GetValue<string>() == "chat-1"));
    }

    [TestMethod]
    public void RendererUsesAtMostThreeButtonsPerActionRow()
    {
        var renderer = new FeishuCardRenderer();
        var card = renderer.PendingInput(
            Session(),
            "input-1",
            new("q1", "选择功能", "请选择", true, true, false,
                ["Codex", "OpenCode", "Claude Code", "第四项", "第五项"]),
            0,
            1);

        var rows = card.Content["elements"]!.AsArray()
            .Where(node => node?["tag"]?.GetValue<string>() == "action")
            .Select(node => node!["actions"]!.AsArray().Count)
            .ToArray();

        Assert.IsTrue(rows.Length >= 2);
        Assert.IsTrue(rows.All(count => count <= 3));
    }

    [TestMethod]
    public void CommandMenuContainsSixGlobalActionsInThreeButtonRows()
    {
        var card = new FeishuCardRenderer().CommandMenu();
        var rows = ActionRows(card).ToArray();
        var actions = rows
            .SelectMany(node => node["actions"]!.AsArray())
            .Select(node => ActionName(node!))
            .ToArray();

        Assert.IsTrue(rows.All(row => row["actions"]!.AsArray().Count <= 3));
        CollectionAssert.AreEqual(
            new[]
            {
                FeishuCardActions.CommandNew,
                FeishuCardActions.CommandSessions,
                FeishuCardActions.CommandStatus,
                FeishuCardActions.CommandWorkspace,
                FeishuCardActions.CommandAliases,
                FeishuCardActions.CommandHelp,
            },
            actions);
        StringAssert.Contains(CardText(card), "不依赖某个活跃 CLI 会话");
    }

    [TestMethod]
    public void RuntimeSelectionMatchesNodeRuntimeChoicesAndCarriesFlowContext()
    {
        var context = RuntimeContext();
        var card = new FeishuCardRenderer().RuntimeSelection("K:\\workspace", context);
        var actions = ActionRows(card)
            .SelectMany(node => node["actions"]!.AsArray())
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "codex", "claudecode", "opencode" },
            actions.Select(node => ActionValue(node!)["runtime"]!.GetValue<string>()).ToArray());
        Assert.IsTrue(actions.All(node =>
            ActionName(node!) == FeishuCardActions.RuntimeNewSelect &&
            ActionValue(node!)["flowId"]!.GetValue<string>() == context.FlowId &&
            ActionValue(node!)["sourceMessageId"]!.GetValue<string>() ==
                context.SourceMessageId &&
            ActionValue(node!)["chatId"]!.GetValue<string>() == context.ChatId));
        StringAssert.Contains(CardText(card), "三个运行环境都是 /新建 的二级选项");
    }

    [TestMethod]
    public void RuntimeProjectFormRequiresProjectNameAndUsesExplicitCallbacks()
    {
        var renderer = new FeishuCardRenderer();
        foreach (var runtime in new[] { "codex", "claudecode", "opencode" })
        {
            var card = renderer.RuntimeProjectForm(runtime, "K:\\workspace", RuntimeContext());
            var form = card.Content["elements"]!.AsArray()
                .Single(node => node?["tag"]?.GetValue<string>() == "form")!;
            var input = form["elements"]!.AsArray()
                .Single(node => node?["tag"]?.GetValue<string>() == "input")!;
            var buttons = Descendants(form)
                .OfType<System.Text.Json.Nodes.JsonObject>()
                .Where(node => node["tag"]?.GetValue<string>() == "button")
                .ToArray();

            Assert.IsTrue(input["required"]!.GetValue<bool>());
            Assert.AreEqual("project_name", input["name"]!.GetValue<string>());
            CollectionAssert.AreEquivalent(
                new[]
                {
                    FeishuCardActions.RuntimeNewSubmit,
                    FeishuCardActions.RuntimeNewCancel,
                },
                buttons.Select(ActionName).ToArray());
            Assert.IsTrue(buttons.All(button =>
                button["complex_interaction"]!.GetValue<bool>() &&
                button["behaviors"]![0]!["type"]!.GetValue<string>() == "callback"));
            var submit = buttons.Single(button =>
                ActionName(button) == FeishuCardActions.RuntimeNewSubmit);
            Assert.AreEqual("form_submit", submit["action_type"]!.GetValue<string>());
        }
    }

    [TestMethod]
    public void RuntimeLaunchResultCardsContainNoRepeatableActions()
    {
        var renderer = new FeishuCardRenderer();
        var submitted = renderer.RuntimeLaunchSubmitted(
            "codex",
            "我的项目",
            "K:\\workspace");
        var cancelled = renderer.RuntimeLaunchCancelled("opencode");

        Assert.IsFalse(Descendants(submitted.Content)
            .OfType<System.Text.Json.Nodes.JsonObject>()
            .Any(node => node["tag"]?.GetValue<string>() == "button"));
        Assert.IsFalse(Descendants(cancelled.Content)
            .OfType<System.Text.Json.Nodes.JsonObject>()
            .Any(node => node["tag"]?.GetValue<string>() == "button"));
        StringAssert.Contains(CardText(submitted), "已提交新建请求");
        StringAssert.Contains(CardText(cancelled), "已取消新建");
    }

    [TestMethod]
    public void ApprovalCardContainsExactlyThreeKnownActions()
    {
        var card = new FeishuCardRenderer().PendingApproval(Session(), Approval());
        var actions = card.Content["elements"]!.AsArray()
            .Where(node => node?["tag"]?.GetValue<string>() == "action")
            .SelectMany(node => node!["actions"]!.AsArray())
            .Select(node => node!["behaviors"]![0]!["value"]!["action"]!.GetValue<string>())
            .ToArray();

        CollectionAssert.AreEqual(
            new[]
            {
                FeishuCardActions.ApprovalAllow,
                FeishuCardActions.ApprovalDeny,
                FeishuCardActions.ApprovalDesktop,
            },
            actions);
    }

    [TestMethod]
    public void RuntimeErrorCardsAreChunkedRedactedAndExposeOnlyFinalRetryAction()
    {
        var error = $"API_TOKEN=secret-value\n{new string('x', 3_000)}";
        var cards = new FeishuCardRenderer().RuntimeError(
            Session(),
            error,
            new("cycle-1", "scheduled", 1, 3, 5));

        Assert.AreEqual(2, cards.Count);
        Assert.IsFalse(CardText(cards[0]).Contains("secret-value", StringComparison.Ordinal));
        StringAssert.Contains(CardText(cards[0]), "API_TOKEN=[已隐藏]");
        StringAssert.Contains(CardText(cards[1]), "5 秒后自动重试");
        Assert.AreEqual(0, ActionRows(cards[0]).Count());
        var button = ActionRows(cards[1]).Single()["actions"]!.AsArray().Single()!;
        Assert.AreEqual(FeishuCardActions.RetryStop, ActionName(button));
        Assert.AreEqual("session-1", ActionValue(button)["sessionId"]!.GetValue<string>());
        Assert.AreEqual("cycle-1", ActionValue(button)["retryCycleId"]!.GetValue<string>());
    }

    [TestMethod]
    public void StoppedRuntimeErrorCardRemovesRetryAction()
    {
        var card = new FeishuCardRenderer().RuntimeError(
            Session(),
            "HTTP 503 Service Unavailable",
            new("cycle-1", "stopped", 2, 3)).Single();

        Assert.AreEqual(0, ActionRows(card).Count());
        StringAssert.Contains(CardText(card), "已停止自动重试");
        StringAssert.Contains(CardText(card), "仍可以从飞书或电脑端重新发送任务");
    }

    [TestMethod]
    public void ReleasedPatchClaimDoesNotLeaveStaleEvictionEntry()
    {
        var ledger = new InMemoryFeishuCardPatchLedger(2);

        Assert.IsTrue(ledger.TryClaim("message-1", "revision-1"));
        ledger.Release("message-1", "revision-1");
        Assert.IsTrue(ledger.TryClaim("message-1", "revision-1"));
        Assert.IsTrue(ledger.TryClaim("message-2", "revision-1"));

        Assert.IsFalse(ledger.TryClaim("message-1", "revision-1"));
    }

    private static FeishuInteractionCoordinator Coordinator(RecordingFeishuGateway gateway) =>
        new(gateway, new FeishuCardRenderer(), new InMemoryFeishuCardPatchLedger());

    private static FeishuSessionView Session() =>
        new("session-1", "codex", "项目 #1234", "K:\\project");

    private static FeishuApprovalView Approval() =>
        new("approval-1", "shell_command", "git status");

    private static IReadOnlyList<FeishuInputQuestionView> Questions() =>
        [
            new("q1", "运行时", "选择运行时", false, false, false, ["Codex", "OpenCode"]),
            new("q2", "模式", "选择模式", true, true, false, ["快速", "完整"]),
        ];

    private static FeishuRuntimeNewContext RuntimeContext() =>
        new("flow-1", "source-message-1", "chat-1");

    private static IEnumerable<System.Text.Json.Nodes.JsonNode> ActionRows(
        FeishuCardView card) => card.Content["elements"]!.AsArray()
            .Where(node => node?["tag"]?.GetValue<string>() == "action")
            .Select(node => node!);

    private static string ActionName(System.Text.Json.Nodes.JsonNode button) =>
        ActionValue(button)["action"]!.GetValue<string>();

    private static System.Text.Json.Nodes.JsonNode ActionValue(
        System.Text.Json.Nodes.JsonNode button) => button["behaviors"]![0]!["value"]!;

    private static string CardText(FeishuCardView card) => string.Join(
        "\n",
        Descendants(card.Content)
            .OfType<System.Text.Json.Nodes.JsonValue>()
            .Select(value => value.TryGetValue<string>(out var text) ? text : null)
            .Where(text => text is not null));

    private static IEnumerable<System.Text.Json.Nodes.JsonNode> Descendants(
        System.Text.Json.Nodes.JsonNode node)
    {
        yield return node;
        if (node is System.Text.Json.Nodes.JsonObject objectNode)
        {
            foreach (var child in objectNode.Select(property => property.Value)
                .Where(child => child is not null))
            {
                foreach (var descendant in Descendants(child!))
                {
                    yield return descendant;
                }
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray arrayNode)
        {
            foreach (var child in arrayNode.Where(child => child is not null))
            {
                foreach (var descendant in Descendants(child!))
                {
                    yield return descendant;
                }
            }
        }
    }
}
