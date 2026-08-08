using System.Net;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActiveFeishuApprovalNotificationCoordinatorTests
{
    private static readonly DateTimeOffset Origin =
        DateTimeOffset.Parse("2026-08-09T00:00:00Z");

    [TestMethod]
    public async Task SendsPendingCardAndPersistsMessageAndRouteIdempotently()
    {
        var store = new RecordingStoreOwner(StoreSnapshot());
        var state = new ActivePersistentBusinessStateOwner(
            Options(),
            store,
            new FixedTimeProvider(Origin));
        await state.StartAsync(CancellationToken.None);
        await state.HandleAsync(ApprovalEvent());
        var gateway = new RecordingFeishuGateway();
        var renderer = new FeishuCardRenderer();
        var coordinator = new ActiveFeishuApprovalNotificationCoordinator(
            state,
            store,
            gateway,
            renderer,
            new FeishuInteractionCoordinator(
                gateway,
                renderer,
                new InMemoryFeishuCardPatchLedger()),
            new RecordingSessionGroupCoordinator(["chat-owner"]));

        await coordinator.NotifyPendingAsync("approval-1", "session-1");
        await coordinator.NotifyPendingAsync("approval-1", "session-1");

        Assert.AreEqual(1, gateway.Sends.Count);
        Assert.AreEqual("chat-owner", gateway.Sends[0].ChatId);
        Assert.AreEqual(32, gateway.Sends[0].IdempotencyKey.Length);
        StringAssert.Contains(
            gateway.Sends[0].Card.Content.ToJsonString(),
            FeishuCardActions.ApprovalAllow);
        CollectionAssert.AreEqual(
            new[] { "message-1" },
            state.Snapshot.Approvals.Requests["approval-1"].MessageIds.ToArray());
        CollectionAssert.AreEqual(
            new[] { "message-1" },
            store.Current.Approvals.Requests["approval-1"].MessageIds.ToArray());
        var route = store.Current.Routes.Messages["message-1"];
        Assert.AreEqual("approval", route.Kind);
        Assert.AreEqual("approval-1", route.RequestId);
        Assert.AreEqual("session-1", route.SessionId);
        Assert.AreEqual("chat-owner", route.ChatId);
    }

    [TestMethod]
    public async Task StartupSynchronizesHistoricalResolvedAndOrphanedCards()
    {
        var store = new RecordingStoreOwner(TerminalStoreSnapshot());
        var state = new ActivePersistentBusinessStateOwner(
            Options(),
            store,
            new FixedTimeProvider(Origin.AddMinutes(5)));
        await state.StartAsync(CancellationToken.None);
        var gateway = new RecordingFeishuGateway();
        var renderer = new FeishuCardRenderer();
        var coordinator = new ActiveFeishuApprovalNotificationCoordinator(
            state,
            store,
            gateway,
            renderer,
            new FeishuInteractionCoordinator(
                gateway,
                renderer,
                new InMemoryFeishuCardPatchLedger()),
            new RecordingSessionGroupCoordinator([]));

        await coordinator.StartAsync(CancellationToken.None);
        await coordinator.StopAsync(CancellationToken.None);

        Assert.AreEqual(2, gateway.Patches.Count);
        var resolved = gateway.Patches.Single(item => item.MessageId == "message-resolved");
        var orphaned = gateway.Patches.Single(item => item.MessageId == "message-orphaned");
        StringAssert.Contains(CardText(resolved.Card), "已批准");
        StringAssert.Contains(CardText(orphaned.Card), "审批已失效，无需再处理");
        Assert.IsTrue(gateway.Patches.All(item =>
            !item.Card.Content.ToJsonString().Contains(
                FeishuCardActions.ApprovalAllow,
                StringComparison.Ordinal)));
    }

    private static string CardText(FeishuCardView card) => string.Join(
        '\n',
        card.Content["elements"]!.AsArray()
            .Select(element => element?["text"]?["content"]?.GetValue<string>())
            .Where(text => text is not null));

    private static BridgeHostOptions Options() => new(
        Path.GetTempPath(),
        IPAddress.Loopback,
        0,
        BridgeOwnershipMode.Active,
        "approval-notification-test");

    private static RuntimeEventEnvelope ApprovalEvent() => new()
    {
        ProtocolVersion = BridgeProtocolVersion.Current,
        Runtime = RuntimeNames.Codex,
        Session = new RuntimeSessionReference
        {
            ExternalId = "session-1",
            Cwd = "K:/repo",
        },
        TraceId = "trace-approval-1",
        CorrelationId = "turn-1",
        EventId = "event-approval-1",
        EventType = RuntimeEventTypes.ApprovalRequested,
        OccurredAt = Origin.AddMinutes(2).ToString("O"),
        Payload = JsonSerializer.SerializeToElement(new
        {
            requestId = "approval-1",
            title = "shell_command",
            description = "git status",
            expiresAt = Origin.AddMinutes(22).ToString("O"),
        }),
    };

    private static NodeStoreSnapshot StoreSnapshot()
    {
        var session = new SessionStoreRecord
        {
            SessionId = "session-1",
            ShortId = "12345678",
            Cwd = "K:/repo",
            ProjectName = "repo",
            Status = SessionStatuses.Running,
            Runtime = RuntimeNames.Codex,
            OpenedAt = Origin.ToString("O"),
            LastSeenAt = Origin.AddMinutes(1).ToString("O"),
        };
        return new(
            new BindingStoreDocument
            {
                OwnerOpenId = "owner",
                Users = new Dictionary<string, BindingStoreRecord>(StringComparer.Ordinal)
                {
                    ["owner"] = new()
                    {
                        OpenId = "owner",
                        ChatId = "chat-owner",
                        ChatType = "p2p",
                        BoundAt = Origin.ToString("O"),
                    },
                },
            },
            new SessionStoreDocument
            {
                Sessions = new Dictionary<string, SessionStoreRecord>(StringComparer.Ordinal)
                {
                    [session.SessionId] = session,
                },
            },
            new RouteStoreDocument(),
            new ApprovalStoreDocument(),
            new SettingsStoreDocument(),
            new ControlTokenStoreDocument());
    }

    private static NodeStoreSnapshot TerminalStoreSnapshot()
    {
        var store = StoreSnapshot();
        store.Approvals.Requests = new Dictionary<string, ApprovalStoreRecord>(
            StringComparer.Ordinal)
        {
            ["approval-resolved"] = new()
            {
                RequestId = "approval-resolved",
                SessionId = "session-1",
                TurnId = "turn-resolved",
                Cwd = "K:/repo",
                ToolName = "shell_command",
                ToolPreview = "git status",
                CreatedAt = Origin.ToString("O"),
                ExpiresAt = Origin.AddMinutes(20).ToString("O"),
                Status = ApprovalStatuses.Resolved,
                MessageIds = ["message-resolved"],
                Resolution = ApprovalResolutions.Allow,
                ResolvedAt = Origin.AddMinutes(2).ToString("O"),
            },
            ["approval-orphaned"] = new()
            {
                RequestId = "approval-orphaned",
                SessionId = "session-1",
                TurnId = "turn-orphaned",
                Cwd = "K:/repo",
                ToolName = "shell_command",
                ToolPreview = "git diff",
                CreatedAt = Origin.ToString("O"),
                ExpiresAt = Origin.AddMinutes(20).ToString("O"),
                Status = ApprovalStatuses.Orphaned,
                MessageIds = ["message-orphaned"],
                Resolution = ApprovalResolutions.Local,
                ResolvedAt = Origin.AddMinutes(3).ToString("O"),
            },
        };
        return store;
    }

    private sealed class RecordingStoreOwner(NodeStoreSnapshot current) :
        IBridgeProductionStoreOwner
    {
        private NodeStoreSnapshot current = current;

        public NodeStoreSnapshot Current => current;

        public BridgeProductionStoreSnapshot Snapshot => new(
            BridgeProductionStoreState.Open,
            current,
            6);

        public ValueTask OpenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<NodeStoreSnapshot> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(current);
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask UpdateAsync(
            Func<NodeStoreSnapshot, NodeStoreSnapshot> update,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = update(current);
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class RecordingSessionGroupCoordinator(IReadOnlyList<string> chats) :
        IBridgeActiveSessionGroupCoordinator
    {
        public ValueTask<SessionStoreRecord?> EnsureAsync(
            string sessionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<SessionStoreRecord?>(null);

        public ValueTask<BridgeSessionGroupRetryResult> RetryAsync(
            string sessionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new BridgeSessionGroupRetryResult(
                false,
                false,
                null,
                null,
                "not used"));

        public ValueTask<IReadOnlyList<string>> NotificationChatsAsync(
            string sessionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(chats);

        public void ScheduleEnsure(string sessionId)
        {
        }
    }

    private sealed class RecordingFeishuGateway : IFeishuGateway
    {
        public List<SentCard> Sends { get; } = [];
        public List<(string MessageId, FeishuCardView Card)> Patches { get; } = [];

        public Task<string> SendCardAsync(
            string chatId,
            FeishuCardView card,
            string? idempotencyKey = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = idempotencyKey ?? throw new AssertFailedException("缺少幂等键。");
            var existing = Sends.SingleOrDefault(item => item.IdempotencyKey == key);
            if (existing is not null)
            {
                return Task.FromResult(existing.MessageId);
            }
            var sent = new SentCard($"message-{Sends.Count + 1}", chatId, key, card);
            Sends.Add(sent);
            return Task.FromResult(sent.MessageId);
        }

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

        private static Task Unexpected() => Task.FromException(
            new AssertFailedException("审批通知不应调用这个飞书端口。"));

        private static Task<T> Unexpected<T>() => Task.FromException<T>(
            new AssertFailedException("审批通知不应调用这个飞书端口。"));
    }

    private sealed record SentCard(
        string MessageId,
        string ChatId,
        string IdempotencyKey,
        FeishuCardView Card);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
