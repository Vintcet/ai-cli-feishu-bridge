using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActiveFeishuApprovalCoordinatorTests
{
    private static readonly DateTimeOffset Origin =
        DateTimeOffset.Parse("2026-08-07T00:00:00Z");

    [TestMethod]
    public async Task AllowDispatchesStandardCommandThenCommitsStateAndCards()
    {
        var fixture = Fixture.Create(RuntimeNames.Codex);

        var result = await fixture.Coordinator.HandleAsync(
            Intent(FeishuIntentTypes.ApprovalResolve, ApprovalResolutions.Allow),
            fixture.Store);

        Assert.AreEqual("success", result.ToastType);
        var command = fixture.Runtime.Commands.Single();
        Assert.AreEqual(RuntimeCommandTypes.ApprovalResolve, command.CommandType);
        Assert.AreEqual(RuntimeNames.Codex, command.Runtime);
        Assert.AreEqual("approval-1", command.Payload.GetProperty("requestId").GetString());
        Assert.AreEqual("allow_once", command.Payload.GetProperty("decision").GetString());
        Assert.IsTrue(BridgeProtocolValidator.Validate(command).IsValid);
        Assert.AreEqual(
            ApprovalStatuses.Resolved,
            fixture.State.Snapshot.Approvals.Requests["approval-1"].Status);
        Assert.AreEqual(
            ApprovalResolutions.Allow,
            fixture.State.Snapshot.Approvals.Requests["approval-1"].Resolution);
        Assert.AreEqual(
            SessionStatuses.Running,
            fixture.State.Snapshot.Sessions.Sessions["session-1"].Status);
        CollectionAssert.AreEquivalent(
            new[] { "card-owner", "card-group" },
            fixture.Gateway.Patches.Select(item => item.MessageId).ToArray());
        Assert.IsTrue(fixture.Gateway.Patches.All(item =>
            CardText(item.Card).Contains("已批准", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task OpenCodeDenyUsesDenyDecisionAndWaitingState()
    {
        var fixture = Fixture.Create(RuntimeNames.OpenCode);

        var result = await fixture.Coordinator.HandleAsync(
            Intent(FeishuIntentTypes.ApprovalResolve, ApprovalResolutions.Deny),
            fixture.Store);

        Assert.AreEqual("success", result.ToastType);
        var command = fixture.Runtime.Commands.Single();
        Assert.AreEqual(RuntimeNames.OpenCode, command.Runtime);
        Assert.AreEqual("deny", command.Payload.GetProperty("decision").GetString());
        Assert.AreEqual(
            SessionStatuses.Waiting,
            fixture.State.Snapshot.Sessions.Sessions["session-1"].Status);
        Assert.AreEqual(
            ApprovalResolutions.Deny,
            fixture.State.Snapshot.Approvals.Requests["approval-1"].Resolution);
    }

    [TestMethod]
    public async Task DeferPersistsDesktopFlagWithoutRuntimeDecisionOrResolution()
    {
        var fixture = Fixture.Create(RuntimeNames.Codex, ready: false);

        var result = await fixture.Coordinator.HandleAsync(
            Intent(FeishuIntentTypes.ApprovalDeferToLocal),
            fixture.Store);

        Assert.AreEqual("success", result.ToastType);
        Assert.AreEqual(0, fixture.Runtime.Commands.Count);
        Assert.AreEqual(
            ApprovalStatuses.Pending,
            fixture.State.Snapshot.Approvals.Requests["approval-1"].Status);
        Assert.AreEqual(
            SessionStatuses.PendingApproval,
            fixture.State.Snapshot.Sessions.Sessions["session-1"].Status);
        Assert.IsTrue(
            fixture.State.Store.Approvals.Requests["approval-1"]
                .ExtensionData!["desktopApprovalRequested"].GetBoolean());
        Assert.AreEqual(2, fixture.Gateway.Patches.Count);
        Assert.IsTrue(fixture.Gateway.Patches.All(item =>
            CardText(item.Card).Contains(
                "已转回 PC 审批",
                StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task QuotedTextAllowUsesThePersistedApprovalRoute()
    {
        var fixture = Fixture.Create(RuntimeNames.Codex);

        var result = await fixture.Coordinator.TryHandleQuotedReplyAsync(
            QuotedIntent("批准", "parentMessageId", "card-owner"),
            fixture.Store);

        Assert.AreEqual("success", result!.ToastType);
        Assert.AreEqual(1, fixture.Runtime.Commands.Count);
        Assert.AreEqual(
            ApprovalStatuses.Resolved,
            fixture.State.Snapshot.Approvals.Requests["approval-1"].Status);
        StringAssert.Contains(result.ToastContent, "已批准");
    }

    [TestMethod]
    public async Task QuotedTextDesktopCanResolveAThreadRootRouteWithoutClaimingDecision()
    {
        var fixture = Fixture.Create(RuntimeNames.Codex, ready: false);

        var result = await fixture.Coordinator.TryHandleQuotedReplyAsync(
            QuotedIntent("电脑审批！", "rootMessageId", "card-group"),
            fixture.Store);

        Assert.AreEqual("success", result!.ToastType);
        Assert.AreEqual(0, fixture.Runtime.Commands.Count);
        Assert.AreEqual(
            ApprovalStatuses.Pending,
            fixture.State.Snapshot.Approvals.Requests["approval-1"].Status);
        StringAssert.Contains(
            result.ToastContent,
            "电脑端审批窗口将在下一次状态刷新时弹出");
        Assert.IsTrue(
            fixture.Store.Approvals.Requests["approval-1"]
                .ExtensionData!["desktopApprovalRequested"].GetBoolean());
    }

    [TestMethod]
    public async Task QuotedTextWithUnknownApprovalActionReturnsUsageWithoutClaiming()
    {
        var fixture = Fixture.Create(RuntimeNames.Codex);

        var result = await fixture.Coordinator.TryHandleQuotedReplyAsync(
            QuotedIntent("继续", "parentMessageId", "card-owner"),
            fixture.Store);

        Assert.AreEqual("info", result!.ToastType);
        StringAssert.Contains(result.ToastContent, "批准");
        Assert.AreEqual(0, fixture.Runtime.Commands.Count);
        Assert.AreEqual(0, fixture.State.Snapshot.Approvals.Claims.Count);
    }

    [TestMethod]
    public async Task TamperedTargetInvalidDecisionAndUnavailableRuntimeFailClosed()
    {
        var fixture = Fixture.Create(RuntimeNames.Codex, ready: false);

        var tampered = await fixture.Coordinator.HandleAsync(
            Intent(
                FeishuIntentTypes.ApprovalResolve,
                ApprovalResolutions.Allow,
                sessionId: "other-session"),
            fixture.Store);
        var invalid = await fixture.Coordinator.HandleAsync(
            Intent(FeishuIntentTypes.ApprovalResolve, "allow_session"),
            fixture.Store);
        var unavailable = await fixture.Coordinator.HandleAsync(
            Intent(FeishuIntentTypes.ApprovalResolve, ApprovalResolutions.Allow),
            fixture.Store);

        Assert.AreEqual("error", tampered.ToastType);
        Assert.AreEqual("error", invalid.ToastType);
        Assert.AreEqual("warning", unavailable.ToastType);
        Assert.AreEqual(0, fixture.Runtime.Commands.Count);
        Assert.AreEqual(0, fixture.State.Snapshot.Approvals.Claims.Count);
        Assert.AreEqual(
            ApprovalStatuses.Pending,
            fixture.State.Snapshot.Approvals.Requests["approval-1"].Status);
    }

    [TestMethod]
    public async Task ConcurrentClicksDispatchOnlyOneDecision()
    {
        var fixture = Fixture.Create(RuntimeNames.ClaudeCode);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.Handler = async (_, cancellationToken) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
        };

        var firstTask = fixture.Coordinator.HandleAsync(
            Intent(FeishuIntentTypes.ApprovalResolve, ApprovalResolutions.Allow),
            fixture.Store);
        await entered.Task;
        var second = await fixture.Coordinator.HandleAsync(
            Intent(
                FeishuIntentTypes.ApprovalResolve,
                ApprovalResolutions.Deny,
                eventId: "event-2"),
            fixture.Store);
        release.SetResult();
        var first = await firstTask;

        Assert.AreEqual("success", first.ToastType);
        Assert.AreEqual("warning", second.ToastType);
        Assert.AreEqual(1, fixture.Runtime.Commands.Count);
        Assert.AreEqual(
            ApprovalResolutions.Allow,
            fixture.State.Snapshot.Approvals.Requests["approval-1"].Resolution);
    }

    [TestMethod]
    public async Task DispatchFailureReleasesClaimAndAllowsRetry()
    {
        var fixture = Fixture.Create(RuntimeNames.Codex);
        fixture.Runtime.Error = new InvalidOperationException("synthetic failure");
        var intent = Intent(
            FeishuIntentTypes.ApprovalResolve,
            ApprovalResolutions.Allow);

        var failed = await fixture.Coordinator.HandleAsync(intent, fixture.Store);
        fixture.Runtime.Error = null;
        var retried = await fixture.Coordinator.HandleAsync(
            intent with { EventId = "event-2" },
            fixture.Store);

        Assert.AreEqual("warning", failed.ToastType);
        Assert.AreEqual("success", retried.ToastType);
        Assert.AreEqual(2, fixture.Runtime.Commands.Count);
        Assert.AreEqual(0, fixture.State.Snapshot.Approvals.Claims.Count);
        Assert.AreEqual(
            ApprovalResolutions.Allow,
            fixture.State.Snapshot.Approvals.Requests["approval-1"].Resolution);
    }

    private static FeishuIntent Intent(
        string intentType,
        string? resolution = null,
        string sessionId = "session-1",
        string eventId = "event-1")
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["requestId"] = "approval-1",
            ["sessionId"] = sessionId,
        };
        if (resolution is not null)
        {
            parameters["resolution"] = resolution;
        }
        return new(
            eventId,
            intentType,
            "owner-1",
            "chat-1",
            "card-owner",
            "card",
            $"trace-{eventId}",
            Parameters: parameters);
    }

    private static FeishuIntent QuotedIntent(
        string text,
        string routeParameter,
        string messageId) => new(
        "event-quoted",
        FeishuIntentTypes.MessagePrompt,
        "owner-1",
        "chat-1",
        "reply-message",
        "p2p",
        "trace-quoted",
        text,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [routeParameter] = messageId,
        });

    private static string CardText(FeishuCardView card) => string.Join(
        '\n',
        Descendants(card.Content)
            .OfType<System.Text.Json.Nodes.JsonValue>()
            .Select(value => value.TryGetValue<string>(out var text) ? text : null)
            .Where(text => text is not null));

    private static IEnumerable<System.Text.Json.Nodes.JsonNode> Descendants(
        System.Text.Json.Nodes.JsonNode node)
    {
        yield return node;
        if (node is System.Text.Json.Nodes.JsonObject owner)
        {
            foreach (var child in owner.Select(item => item.Value).Where(item => item is not null))
            {
                foreach (var descendant in Descendants(child!))
                {
                    yield return descendant;
                }
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (var child in array.Where(item => item is not null))
            {
                foreach (var descendant in Descendants(child!))
                {
                    yield return descendant;
                }
            }
        }
    }

    private sealed record Fixture(
        ActiveFeishuApprovalCoordinator Coordinator,
        RecordingApprovalStateOwner State,
        RecordingRuntimeCommandGateway Runtime,
        RecordingFeishuGateway Gateway,
        NodeStoreSnapshot Store)
    {
        public static Fixture Create(string runtime, bool ready = true)
        {
            var store = StoreSnapshot(runtime);
            var state = new RecordingApprovalStateOwner(store);
            var commands = new RecordingRuntimeCommandGateway { Ready = ready };
            var gateway = new RecordingFeishuGateway();
            var interactions = new FeishuInteractionCoordinator(
                gateway,
                new FeishuCardRenderer(),
                new InMemoryFeishuCardPatchLedger());
            return new(
                new(state, commands, interactions),
                state,
                commands,
                gateway,
                store);
        }
    }

    private static NodeStoreSnapshot StoreSnapshot(string runtime)
    {
        var session = new SessionStoreRecord
        {
            SessionId = "session-1",
            ShortId = "12345678",
            Cwd = "K:\\workspace\\project",
            ProjectName = "project",
            Runtime = runtime,
            Status = SessionStatuses.PendingApproval,
            OpenedAt = Origin.ToString("O"),
            LastSeenAt = Origin.AddMinutes(1).ToString("O"),
        };
        var approval = new ApprovalStoreRecord
        {
            RequestId = "approval-1",
            SessionId = session.SessionId,
            TurnId = "turn-1",
            Cwd = session.Cwd,
            ToolName = "shell_command",
            ToolPreview = "git status",
            CreatedAt = Origin.ToString("O"),
            ExpiresAt = Origin.AddMinutes(10).ToString("O"),
            Status = ApprovalStatuses.Pending,
            MessageIds = ["card-owner", "card-group"],
            ExtensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["riskLevel"] = JsonSerializer.SerializeToElement("normal"),
            },
        };
        return new(
            new BindingStoreDocument(),
            new SessionStoreDocument
            {
                Sessions = new Dictionary<string, SessionStoreRecord>(StringComparer.Ordinal)
                {
                    [session.SessionId] = session,
                },
            },
            new RouteStoreDocument
            {
                Messages = new Dictionary<string, MessageRouteStoreRecord>(
                    StringComparer.Ordinal)
                {
                    ["card-owner"] = new()
                    {
                        MessageId = "card-owner",
                        SessionId = session.SessionId,
                        RequestId = approval.RequestId,
                        ChatId = "chat-1",
                        Kind = "approval",
                        CreatedAt = Origin.ToString("O"),
                    },
                    ["card-group"] = new()
                    {
                        MessageId = "card-group",
                        SessionId = session.SessionId,
                        RequestId = approval.RequestId,
                        ChatId = "chat-1",
                        Kind = "approval",
                        CreatedAt = Origin.ToString("O"),
                    },
                },
            },
            new ApprovalStoreDocument
            {
                Requests = new Dictionary<string, ApprovalStoreRecord>(StringComparer.Ordinal)
                {
                    [approval.RequestId] = approval,
                },
            },
            new SettingsStoreDocument(),
            new ControlTokenStoreDocument());
    }

    private sealed class RecordingApprovalStateOwner : IBridgeActiveApprovalStateOwner
    {
        private readonly object sync = new();
        private BridgeBusinessStateSnapshot snapshot;

        public RecordingApprovalStateOwner(NodeStoreSnapshot store)
        {
            Store = store;
            var core = NodeStoreCoreProjection.Project(store);
            snapshot = new(
                true,
                "production",
                1,
                0,
                core.Sessions,
                core.Approvals,
                InputRegistryState.Empty);
        }

        public NodeStoreSnapshot Store { get; private set; }

        public BridgeBusinessStateSnapshot Snapshot
        {
            get
            {
                lock (sync)
                {
                    return snapshot;
                }
            }
        }

        public ValueTask<BridgeApprovalClaim?> TryClaimApprovalAsync(
            string requestId,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (!snapshot.Approvals.Requests.TryGetValue(requestId, out var approval) ||
                    approval.Status != ApprovalStatuses.Pending ||
                    !string.Equals(approval.SessionId, sessionId, StringComparison.Ordinal) ||
                    !snapshot.Sessions.Sessions.TryGetValue(sessionId, out var session))
                {
                    return ValueTask.FromResult<BridgeApprovalClaim?>(null);
                }
                var claim = ApprovalStateMachine.Claim(snapshot.Approvals, requestId);
                if (!claim.Value)
                {
                    return ValueTask.FromResult<BridgeApprovalClaim?>(null);
                }
                snapshot = snapshot with { Approvals = claim.State };
                return ValueTask.FromResult<BridgeApprovalClaim?>(new(approval, session));
            }
        }

        public ValueTask ReleaseApprovalClaimAsync(
            string requestId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                snapshot = snapshot with
                {
                    Approvals = ApprovalStateMachine.ReleaseClaim(
                        snapshot.Approvals,
                        requestId),
                };
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<BridgeApprovalDelivery?> RecordApprovalDeliveryAsync(
            string requestId,
            string sessionId,
            string messageId,
            string chatId,
            DateTimeOffset createdAt,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<BridgeApprovalDelivery?>(null);

        public ValueTask<BridgeApprovalClaim?> ResolveClaimedApprovalAsync(
            string requestId,
            string sessionId,
            string resolution,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                var resolved = ApprovalStateMachine.ResolveClaimed(
                    snapshot.Approvals,
                    requestId,
                    resolution,
                    Origin.AddMinutes(2));
                if (!resolved.Value)
                {
                    snapshot = snapshot with { Approvals = resolved.State };
                    return ValueTask.FromResult<BridgeApprovalClaim?>(null);
                }
                var sessions = SessionStateMachine.Transition(
                    snapshot.Sessions,
                    sessionId,
                    resolution == ApprovalResolutions.Allow
                        ? SessionStatuses.Running
                        : SessionStatuses.Waiting,
                    Origin.AddMinutes(2));
                snapshot = snapshot with
                {
                    Revision = snapshot.Revision + 1,
                    Sessions = sessions,
                    Approvals = resolved.State,
                };
                return ValueTask.FromResult<BridgeApprovalClaim?>(new(
                    snapshot.Approvals.Requests[requestId],
                    snapshot.Sessions.Sessions[sessionId]));
            }
        }

        public ValueTask<BridgeApprovalClaim?> DeferClaimedApprovalAsync(
            string requestId,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (!snapshot.Approvals.Claims.Contains(requestId) ||
                    !snapshot.Approvals.Requests.TryGetValue(requestId, out var approval) ||
                    approval.Status != ApprovalStatuses.Pending ||
                    !string.Equals(approval.SessionId, sessionId, StringComparison.Ordinal) ||
                    !snapshot.Sessions.Sessions.TryGetValue(sessionId, out var session))
                {
                    return ValueTask.FromResult<BridgeApprovalClaim?>(null);
                }
                snapshot = snapshot with
                {
                    Revision = snapshot.Revision + 1,
                    Approvals = ApprovalStateMachine.ReleaseClaim(
                        snapshot.Approvals,
                        requestId),
                };
                Store.Approvals.Requests[requestId].ExtensionData![
                    "desktopApprovalRequested"] = JsonSerializer.SerializeToElement(true);
                return ValueTask.FromResult<BridgeApprovalClaim?>(new(approval, session));
            }
        }
    }

    private sealed class RecordingRuntimeCommandGateway : IBridgeRuntimeCommandGateway
    {
        private readonly object sync = new();

        public List<RuntimeCommandEnvelope> Commands { get; } = [];
        public bool Ready { get; set; }
        public Exception? Error { get; set; }
        public Func<RuntimeCommandEnvelope, CancellationToken, Task>? Handler { get; set; }

        public bool IsReady(string runtime, RuntimeSession session) => Ready;

        public async Task DispatchAsync(
            RuntimeCommandEnvelope command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                Commands.Add(command);
            }
            if (Error is not null)
            {
                throw Error;
            }
            if (Handler is not null)
            {
                await Handler(command, cancellationToken);
            }
        }
    }

    private sealed class RecordingFeishuGateway : IFeishuGateway
    {
        public List<(string MessageId, FeishuCardView Card)> Patches { get; } = [];

        public Task PatchCardAsync(
            string messageId,
            FeishuCardView card,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Patches.Add((messageId, card));
            return Task.CompletedTask;
        }

        public Task<string> SendTextAsync(
            string chatId,
            string text,
            CancellationToken cancellationToken = default) => Unexpected<string>();

        public Task<string> ReplyTextAsync(
            string messageId,
            string text,
            CancellationToken cancellationToken = default) => Unexpected<string>();

        public Task<string> SendCardAsync(
            string chatId,
            FeishuCardView card,
            string? idempotencyKey = null,
            CancellationToken cancellationToken = default) => Unexpected<string>();

        public Task<FeishuSessionGroup> CreateSessionGroupAsync(
            string ownerOpenId,
            string name,
            string description,
            CancellationToken cancellationToken = default) => Unexpected<FeishuSessionGroup>();

        public Task UpdateSessionGroupNameAsync(
            string chatId,
            string name,
            CancellationToken cancellationToken = default) => Unexpected();

        public Task DeleteSessionGroupAsync(
            string chatId,
            CancellationToken cancellationToken = default) => Unexpected();

        public Task<long> DownloadMessageResourceAsync(
            string messageId,
            string fileKey,
            string resourceType,
            string destinationPath,
            long maxBytes,
            CancellationToken cancellationToken = default) => Unexpected<long>();

        public Task<string> SendLocalFileAsync(
            string chatId,
            string filePath,
            CancellationToken cancellationToken = default) => Unexpected<string>();

        private static Task Unexpected() =>
            Task.FromException(new AssertFailedException("审批协调器不应调用这个飞书端口。"));

        private static Task<T> Unexpected<T>() =>
            Task.FromException<T>(new AssertFailedException(
                "审批协调器不应调用这个飞书端口。"));
    }
}
