using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class BridgeBusinessStateOwnerTests
{
    private static readonly DateTimeOffset Origin =
        DateTimeOffset.Parse("2026-08-06T10:00:00Z");

    [TestMethod]
    public async Task OwnerInitializesFromShadowAndAppliesOrderedCoreTransitions()
    {
        var store = LoadedStore(SessionDirectoryState.Empty, ApprovalRegistryState.Empty);
        var owner = new BridgeBusinessStateOwner(store);
        await owner.StartAsync(CancellationToken.None);
        using var ingress = new BridgeRuntimeEventIngress([owner]);

        await ingress.PublishAsync(Event(
            "session-started",
            RuntimeEventTypes.SessionStarted,
            Origin,
            new { }));
        await ingress.PublishAsync(Event(
            "turn-started",
            RuntimeEventTypes.TurnStarted,
            Origin.AddMinutes(1),
            new { turnId = "turn-1" }));
        await ingress.PublishAsync(Event(
            "approval-requested",
            RuntimeEventTypes.ApprovalRequested,
            Origin.AddMinutes(2),
            new
            {
                requestId = "approval-1",
                title = "执行命令",
                expiresAt = Origin.AddMinutes(22).ToString("O"),
            }));
        await ingress.PublishAsync(Event(
            "approval-resolved",
            RuntimeEventTypes.ApprovalResolvedExternally,
            Origin.AddMinutes(3),
            new { requestId = "approval-1", outcome = "allowed" }));
        await ingress.PublishAsync(Event(
            "input-requested",
            RuntimeEventTypes.InputRequested,
            Origin.AddMinutes(4),
            new
            {
                requestId = "input-1",
                expiresAt = Origin.AddMinutes(24).ToString("O"),
                questions = new[]
                {
                    new
                    {
                        id = "q1",
                        prompt = "继续吗？",
                        options = new[] { "继续", "停止" },
                        multiple = false,
                        allowsCustom = false,
                    },
                },
            }));
        await ingress.PublishAsync(Event(
            "input-resolved",
            RuntimeEventTypes.InputResolvedExternally,
            Origin.AddMinutes(5),
            new { requestId = "input-1" }));
        await ingress.PublishAsync(Event(
            "session-ended",
            RuntimeEventTypes.SessionEnded,
            Origin.AddMinutes(6),
            new { reason = "closed" }));

        var state = owner.Snapshot;
        Assert.IsTrue(state.Initialized);
        Assert.AreEqual(7, state.Revision);
        Assert.AreEqual(SessionStatuses.Ended, state.Sessions.Sessions["session-1"].Status);
        Assert.AreEqual(
            ApprovalResolutions.Allow,
            state.Approvals.Requests["approval-1"].Resolution);
        Assert.AreEqual(
            InputRequestStatuses.Local,
            state.Inputs.Requests["input-1"].Status);
        Assert.IsFalse(state.Inputs.Requests["input-1"].Questions[0].AllowsCustom);
        Assert.AreEqual("passive", owner.ComponentHealth.Status);
    }

    [TestMethod]
    public async Task CompletedEventDeduplicationDoesNotAdvanceOwnerRevision()
    {
        var owner = new BridgeBusinessStateOwner(
            LoadedStore(SessionDirectoryState.Empty, ApprovalRegistryState.Empty));
        await owner.StartAsync(CancellationToken.None);
        using var ingress = new BridgeRuntimeEventIngress([owner]);
        var runtimeEvent = Event(
            "same-event",
            RuntimeEventTypes.SessionStarted,
            Origin,
            new { });

        await ingress.PublishAsync(runtimeEvent);
        await ingress.PublishAsync(runtimeEvent);

        Assert.AreEqual(1, owner.Snapshot.Revision);
        Assert.AreEqual(1, owner.Snapshot.Sessions.Sessions.Count);
    }

    [TestMethod]
    public async Task InvalidOrderFailsWithoutPartiallyMutatingBusinessState()
    {
        var owner = new BridgeBusinessStateOwner(
            LoadedStore(SessionDirectoryState.Empty, ApprovalRegistryState.Empty));
        await owner.StartAsync(CancellationToken.None);
        using var ingress = new BridgeRuntimeEventIngress([owner]);

        await Assert.ThrowsExceptionAsync<KeyNotFoundException>(() =>
            ingress.PublishAsync(Event(
                "turn-before-session",
                RuntimeEventTypes.TurnStarted,
                Origin,
                new { turnId = "turn-1" })));

        Assert.AreEqual(0, owner.Snapshot.Revision);
        Assert.AreEqual(0, owner.Snapshot.Sessions.Sessions.Count);
    }

    [TestMethod]
    public async Task OlderEventFailsWithoutReplacingTheLastValidSnapshot()
    {
        var owner = new BridgeBusinessStateOwner(
            LoadedStore(SessionDirectoryState.Empty, ApprovalRegistryState.Empty));
        await owner.StartAsync(CancellationToken.None);
        using var ingress = new BridgeRuntimeEventIngress([owner]);
        await ingress.PublishAsync(Event(
            "session-started",
            RuntimeEventTypes.SessionStarted,
            Origin.AddMinutes(2),
            new { }));

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            ingress.PublishAsync(Event(
                "older-turn",
                RuntimeEventTypes.TurnStarted,
                Origin.AddMinutes(1),
                new { turnId = "turn-1" })));

        Assert.AreEqual(1, owner.Snapshot.Revision);
        Assert.AreEqual(
            SessionStatuses.Ready,
            owner.Snapshot.Sessions.Sessions["session-1"].Status);
    }

    [TestMethod]
    public async Task ConnectedEventReopensAnEndedSession()
    {
        var owner = new BridgeBusinessStateOwner(
            LoadedStore(SessionDirectoryState.Empty, ApprovalRegistryState.Empty));
        await owner.StartAsync(CancellationToken.None);
        using var ingress = new BridgeRuntimeEventIngress([owner]);
        await ingress.PublishAsync(Event(
            "session-started",
            RuntimeEventTypes.SessionStarted,
            Origin,
            new { }));
        await ingress.PublishAsync(Event(
            "runtime-disconnected",
            RuntimeEventTypes.RuntimeDisconnected,
            Origin.AddMinutes(1),
            new { reason = "closed" }));

        await ingress.PublishAsync(Event(
            "runtime-reconnected",
            RuntimeEventTypes.RuntimeConnected,
            Origin.AddMinutes(2),
            new { endpoint = "terminal-host" }));

        var session = owner.Snapshot.Sessions.Sessions["session-1"];
        Assert.AreEqual(SessionStatuses.Ready, session.Status);
        Assert.AreEqual(Origin.AddMinutes(2), session.OpenedAt);
        Assert.IsNull(session.EndedAt);
        Assert.AreEqual(3, owner.Snapshot.Revision);
    }

    [TestMethod]
    public async Task MissingExternalResolutionFailsWithoutMovingSession()
    {
        var owner = new BridgeBusinessStateOwner(
            LoadedStore(SessionDirectoryState.Empty, ApprovalRegistryState.Empty));
        await owner.StartAsync(CancellationToken.None);
        using var ingress = new BridgeRuntimeEventIngress([owner]);
        await ingress.PublishAsync(Event(
            "session-started",
            RuntimeEventTypes.SessionStarted,
            Origin,
            new { }));

        await Assert.ThrowsExceptionAsync<KeyNotFoundException>(() =>
            ingress.PublishAsync(Event(
                "missing-approval",
                RuntimeEventTypes.ApprovalResolvedExternally,
                Origin.AddMinutes(1),
                new { requestId = "missing", outcome = "allowed" })));
        await Assert.ThrowsExceptionAsync<KeyNotFoundException>(() =>
            ingress.PublishAsync(Event(
                "missing-input",
                RuntimeEventTypes.InputResolvedExternally,
                Origin.AddMinutes(1),
                new { requestId = "missing" })));

        Assert.AreEqual(1, owner.Snapshot.Revision);
        Assert.AreEqual(
            SessionStatuses.Ready,
            owner.Snapshot.Sessions.Sessions["session-1"].Status);
    }

    [TestMethod]
    public async Task SemanticDuplicateExternalResolutionIsANoOp()
    {
        var owner = new BridgeBusinessStateOwner(
            LoadedStore(SessionDirectoryState.Empty, ApprovalRegistryState.Empty));
        await owner.StartAsync(CancellationToken.None);
        using var ingress = new BridgeRuntimeEventIngress([owner]);
        await ingress.PublishAsync(Event(
            "session-started",
            RuntimeEventTypes.SessionStarted,
            Origin,
            new { }));
        await ingress.PublishAsync(Event(
            "input-requested",
            RuntimeEventTypes.InputRequested,
            Origin.AddMinutes(1),
            new
            {
                requestId = "input-1",
                expiresAt = Origin.AddMinutes(21).ToString("O"),
                questions = new[]
                {
                    new { id = "q1", prompt = "继续吗？" },
                },
            }));
        await ingress.PublishAsync(Event(
            "input-resolved",
            RuntimeEventTypes.InputResolvedExternally,
            Origin.AddMinutes(2),
            new { requestId = "input-1" }));
        await ingress.PublishAsync(Event(
            "turn-completed",
            RuntimeEventTypes.TurnCompleted,
            Origin.AddMinutes(3),
            new { turnId = "turn-1" }));
        var revision = owner.Snapshot.Revision;

        await ingress.PublishAsync(Event(
            "input-resolved-again",
            RuntimeEventTypes.InputResolvedExternally,
            Origin.AddMinutes(4),
            new { requestId = "input-1" }));

        Assert.AreEqual(revision, owner.Snapshot.Revision);
        Assert.AreEqual(
            SessionStatuses.Waiting,
            owner.Snapshot.Sessions.Sessions["session-1"].Status);
    }

    [TestMethod]
    public async Task PassiveFeishuIntentIsExplicitlyRejectedWithoutCliOrStoreMutation()
    {
        var initialSessions = SessionStateMachine.Register(
            SessionDirectoryState.Empty,
            new SessionState(
                "stored-session",
                RuntimeNames.Codex,
                "C:/stored",
                SessionStatuses.Waiting,
                Origin,
                Origin));
        var store = LoadedStore(initialSessions, ApprovalRegistryState.Empty);
        var owner = new BridgeBusinessStateOwner(store);
        await owner.StartAsync(CancellationToken.None);
        var ingress = new BridgeFeishuIntentIngress([owner]);

        var result = await ingress.PublishAsync(new FeishuIntent(
            "feishu-event-1",
            FeishuIntentTypes.MessagePrompt,
            "operator-1",
            "chat-1",
            "message-1",
            "group",
            "trace-1",
            Text: "继续"));

        Assert.AreEqual("warning", result!.ToastType);
        StringAssert.Contains(result.ToastContent, "只读观测");
        Assert.AreEqual(1, owner.Snapshot.RejectedFeishuIntents);
        Assert.AreEqual(0, owner.Snapshot.Revision);
        Assert.AreSame(initialSessions, owner.Snapshot.Sessions);
        Assert.AreSame(store.Snapshot.Core!.Sessions, initialSessions);
    }

    [TestMethod]
    public async Task IncompatibleStoreKeepsOwnerUnavailable()
    {
        var store = new FixedStoreShadow(new BridgeStoreShadowSnapshot(
            BridgeStoreShadowStatuses.Incompatible,
            null,
            1,
            0,
            "sessions.json"));
        var owner = new BridgeBusinessStateOwner(store);

        await owner.StartAsync(CancellationToken.None);

        Assert.IsFalse(owner.Snapshot.Initialized);
        Assert.AreEqual("failed", owner.ComponentHealth.Status);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            owner.HandleAsync(Event(
                "session-started",
                RuntimeEventTypes.SessionStarted,
                Origin,
                new { })));
    }

    private static RuntimeEventEnvelope Event(
        string eventId,
        string eventType,
        DateTimeOffset occurredAt,
        object payload) => new()
        {
            ProtocolVersion = BridgeProtocolVersion.Current,
            Runtime = RuntimeNames.Codex,
            Session = new RuntimeSessionReference
            {
                ExternalId = "session-1",
                Cwd = "C:/repo",
            },
            TraceId = $"trace-{eventId}",
            EventId = eventId,
            EventType = eventType,
            OccurredAt = occurredAt.ToString("O"),
            Payload = JsonSerializer.SerializeToElement(payload),
        };

    private static FixedStoreShadow LoadedStore(
        SessionDirectoryState sessions,
        ApprovalRegistryState approvals) => new(
            new BridgeStoreShadowSnapshot(
                BridgeStoreShadowStatuses.Loaded,
                new NodeStoreCoreState(
                    sessions,
                    MessageRouteRegistryState.Empty,
                    approvals),
                4,
                1));

    private sealed class FixedStoreShadow(BridgeStoreShadowSnapshot snapshot)
        : IBridgeStoreShadow
    {
        public BridgeStoreShadowSnapshot Snapshot { get; } = snapshot;

        public BridgeComponentHealth ComponentHealth => new(
            "fixed-store",
            "ready");

        public Task RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
