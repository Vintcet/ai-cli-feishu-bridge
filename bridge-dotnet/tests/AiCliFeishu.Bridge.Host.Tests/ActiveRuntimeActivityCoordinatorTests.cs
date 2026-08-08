using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Encodings.Web;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActiveRuntimeActivityCoordinatorTests
{
    private const string SessionId = "session-activity-1";

    [TestMethod]
    public async Task DisabledSettingDoesNotSendActivityCard()
    {
        await using var fixture = await Fixture.CreateAsync(notifyActivity: false);

        await fixture.Coordinator.RecordAsync(Activity(
            "activity-disabled",
            "turn-1",
            RuntimeActivityKinds.ToolStarted,
            "正在调用命令行"));
        await Task.Delay(80);

        Assert.AreEqual(0, fixture.Gateway.Sends.Count);
        Assert.AreEqual(0, fixture.Gateway.Patches.Count);
    }

    [TestMethod]
    public async Task SameTurnUsesOneMessageAndPatchesLaterActivity()
    {
        await using var fixture = await Fixture.CreateAsync(
            flushInterval: TimeSpan.FromMilliseconds(20));

        await fixture.Coordinator.RecordAsync(Activity(
            "activity-first",
            "turn-1",
            RuntimeActivityKinds.ToolStarted,
            "正在调用命令行",
            "git status"));
        await WaitUntilAsync(() => fixture.Gateway.Sends.Count == 1);

        await fixture.Coordinator.RecordAsync(Activity(
            "activity-second",
            "turn-1",
            RuntimeActivityKinds.ToolCompleted,
            "命令行 已完成",
            "工作区干净"));
        await WaitUntilAsync(() => fixture.Gateway.Patches.Count >= 1);

        Assert.AreEqual(1, fixture.Gateway.Sends.Count);
        Assert.IsTrue(fixture.Gateway.Patches.All(patch =>
            patch.MessageId == fixture.Gateway.Sends[0].MessageId));
        StringAssert.Contains(CardJson(fixture.Gateway.Patches[^1].Card), "命令行 已完成");
        var route = fixture.Store.Current.Routes.Messages.Values.Single();
        Assert.AreEqual("activity", route.Kind);
        Assert.AreEqual(SessionId, route.SessionId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(route.RequestId));
    }

    [TestMethod]
    public async Task TurnChangeCompletesPreviousCardBeforeCreatingNextCard()
    {
        await using var fixture = await Fixture.CreateAsync(
            flushInterval: TimeSpan.FromMilliseconds(40));

        await fixture.Coordinator.RecordAsync(Activity(
            "activity-turn-1",
            "turn-1",
            RuntimeActivityKinds.ToolStarted,
            "正在调用命令行"));
        await WaitUntilAsync(() => fixture.Gateway.Sends.Count == 1);

        await fixture.Coordinator.RecordAsync(Activity(
            "activity-turn-2",
            "turn-2",
            RuntimeActivityKinds.PromptSubmitted,
            "已提交新任务"));
        await WaitUntilAsync(() => fixture.Gateway.Sends.Count == 2);
        await WaitUntilAsync(() => fixture.Gateway.Patches.Count >= 1);

        StringAssert.Contains(
            CardJson(fixture.Gateway.Patches[0].Card),
            "本轮处理完成");
        StringAssert.Contains(
            CardJson(fixture.Gateway.Sends[1].Card),
            "正在处理");
        Assert.AreNotEqual(
            fixture.Gateway.Sends[0].MessageId,
            fixture.Gateway.Sends[1].MessageId);
    }

    [TestMethod]
    public async Task PartialChatFailureRetriesMissingChatAndKeepsStableRoutes()
    {
        var gateway = new RecordingGateway();
        gateway.FailChats.Add("chat-2");
        await using var fixture = await Fixture.CreateAsync(
            chats: ["chat-1", "chat-2"],
            gateway: gateway,
            retryInterval: TimeSpan.FromMilliseconds(20));

        await fixture.Coordinator.RecordAsync(Activity(
            "activity-partial",
            "turn-1",
            RuntimeActivityKinds.ToolStarted,
            "正在调用命令行"));
        await WaitUntilAsync(() => gateway.Attempts.Count >= 2);
        var firstChatKey = gateway.Attempts.Single(attempt =>
            attempt.ChatId == "chat-1").IdempotencyKey;

        gateway.FailChats.Clear();
        await WaitUntilAsync(() => fixture.Store.Current.Routes.Messages.Count == 2);

        Assert.AreEqual(1, gateway.Attempts.Count(attempt => attempt.ChatId == "chat-1"));
        Assert.IsTrue(gateway.Attempts.Count(attempt => attempt.ChatId == "chat-2") >= 2);
        Assert.AreEqual(
            firstChatKey,
            gateway.Attempts.Last(attempt => attempt.ChatId == "chat-1").IdempotencyKey);
        Assert.IsTrue(fixture.Store.Current.Routes.Messages.Values.All(route =>
            route.Kind == "activity"));
    }

    [TestMethod]
    public async Task RestartRehydratesActivityRouteAndPatchesExistingCard()
    {
        var gateway = new RecordingGateway();
        var store = new RecordingStore(StoreSnapshot(true, ["chat-1"]));
        await using (var first = await Fixture.CreateAsync(store: store, gateway: gateway))
        {
            await first.Coordinator.RecordAsync(Activity(
                "activity-before-restart",
                "turn-1",
                RuntimeActivityKinds.ToolStarted,
                "正在调用命令行"));
            await WaitUntilAsync(() => gateway.Sends.Count == 1);
        }

        await using (var recovered = await Fixture.CreateAsync(store: store, gateway: gateway))
        {
            await recovered.Coordinator.RecordAsync(Activity(
                "activity-after-restart",
                "turn-1",
                RuntimeActivityKinds.ToolCompleted,
                "命令行 已完成"));
            await WaitUntilAsync(() => gateway.Patches.Count >= 1);
        }

        Assert.AreEqual(1, gateway.Sends.Count);
        Assert.IsTrue(gateway.Patches.All(patch =>
            patch.MessageId == gateway.Sends[0].MessageId));
    }

    [TestMethod]
    public async Task NoRecipientsClearsActivityMarkerWithoutSending()
    {
        await using var fixture = await Fixture.CreateAsync(
            chats: []);

        await fixture.Coordinator.RecordAsync(Activity(
            "activity-no-recipient",
            "turn-1",
            RuntimeActivityKinds.ToolStarted,
            "正在调用命令行"));
        await WaitUntilAsync(() => ExtensionString(
            fixture.Store.Current.Sessions.Sessions[SessionId],
            "activeActivityKey") is null);

        Assert.AreEqual(0, fixture.Gateway.Sends.Count);
        Assert.IsNull(ExtensionString(
            fixture.Store.Current.Sessions.Sessions[SessionId],
            "activeActivityKey"));
    }

    [TestMethod]
    public async Task FinishedActivityPatchesEvenAfterSessionBecomesEnded()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Coordinator.RecordAsync(Activity(
            "activity-ended",
            "turn-1",
            RuntimeActivityKinds.ToolStarted,
            "正在调用命令行"));
        await WaitUntilAsync(() => fixture.Gateway.Sends.Count == 1);
        fixture.Store.SetSessionStatus(SessionStatuses.Ended);

        await fixture.Coordinator.FinishAsync(
            SessionId,
            "会话已结束",
            "turn-1");
        await WaitUntilAsync(() => fixture.Gateway.Patches.Count >= 1);

        StringAssert.Contains(
            CardJson(fixture.Gateway.Patches[^1].Card),
            "本轮处理完成");
        Assert.AreEqual(1, fixture.Gateway.Sends.Count);
    }

    [TestMethod]
    public async Task CompletionFinishesActivityBeforeStopNotification()
    {
        var store = new RecordingStore(StoreSnapshot(true, ["chat-1"]));
        var gateway = new RecordingGateway();
        var state = new RecordingStateSink();
        var activity = new ActiveRuntimeActivityCoordinator(
            ActiveOptions(),
            store,
            gateway,
            new FeishuCardRenderer(),
            flushInterval: TimeSpan.FromMilliseconds(30),
            retryInterval: TimeSpan.FromMilliseconds(30));
        var retry = new ActiveRuntimeRetryCoordinator(
            ActiveOptions(),
            state,
            store,
            new RecordingCommandGateway(),
            gateway,
            new FeishuCardRenderer(),
            retryDelayOverride: TimeSpan.FromMilliseconds(30),
            activity: activity);
        await retry.StartAsync(CancellationToken.None);

        await retry.HandleAsync(Activity(
            "integration-activity",
            "turn-integration",
            RuntimeActivityKinds.ToolStarted,
            "正在调用命令行"));
        await retry.HandleAsync(new RuntimeEventEnvelope
        {
            ProtocolVersion = BridgeProtocolVersion.Current,
            Runtime = RuntimeNames.Codex,
            Session = new RuntimeSessionReference
            {
                ExternalId = SessionId,
                Cwd = "K:/repo",
            },
            TraceId = "trace-integration-completion",
            CorrelationId = "turn-integration",
            EventId = "integration-completion",
            EventType = RuntimeEventTypes.TurnCompleted,
            OccurredAt = "2026-08-08T00:00:01.000Z",
            Payload = JsonSerializer.SerializeToElement(new
            {
                turnId = "turn-integration",
                message = "任务完成。",
            }),
        });

        Assert.IsTrue(state.Events.Count >= 2);
        var stopIndex = gateway.Operations.FindIndex(operation =>
            operation.Contains("本轮已完成", StringComparison.Ordinal));
        Assert.IsTrue(stopIndex >= 0);
        Assert.IsTrue(gateway.Operations.Take(stopIndex).Any(operation =>
            operation.Contains("本轮处理完成", StringComparison.Ordinal)));
        Assert.IsTrue(store.Current.Routes.Messages.Values.Any(route =>
            route.Kind == "activity"));
        Assert.IsTrue(store.Current.Routes.Messages.Values.Any(route =>
            route.Kind == "stop"));

        await retry.StopAsync(CancellationToken.None);
        retry.Dispose();
        activity.Dispose();
    }

    private static RuntimeEventEnvelope Activity(
        string eventId,
        string? turnId,
        string activityKind,
        string summary,
        string? detail = null) => new()
        {
            ProtocolVersion = BridgeProtocolVersion.Current,
            Runtime = RuntimeNames.Codex,
            Session = new RuntimeSessionReference
            {
                ExternalId = SessionId,
                Cwd = "K:/repo",
            },
            TraceId = $"trace-{eventId}",
            CorrelationId = turnId,
            EventId = eventId,
            EventType = RuntimeEventTypes.TurnActivity,
            OccurredAt = "2026-08-08T00:00:00.000Z",
            Payload = JsonSerializer.SerializeToElement(new
            {
                turnId,
                activityKind,
                summary,
                toolName = "shell_command",
                detail,
            }),
        };

    private static BridgeHostOptions ActiveOptions() => new(
        Path.GetTempPath(),
        IPAddress.Loopback,
        0,
        BridgeOwnershipMode.Active,
        "activity-integration-test");

    private static NodeStoreSnapshot StoreSnapshot(
        bool notifyActivity,
        IReadOnlyList<string> chats,
        string status = SessionStatuses.Waiting)
    {
        var users = chats.Select((chatId, index) => new BindingStoreRecord
            {
                OpenId = $"owner-{index}",
                ChatId = chatId,
                ChatType = "p2p",
                BoundAt = "2026-08-08T00:00:00.000Z",
            })
            .ToDictionary(binding => binding.OpenId, StringComparer.Ordinal);
        var session = new SessionStoreRecord
        {
            SessionId = SessionId,
            ShortId = "activity-1",
            ProjectName = "repo",
            Cwd = "K:/repo",
            Runtime = RuntimeNames.Codex,
            Status = status,
            OpenedAt = "2026-08-08T00:00:00.000Z",
            LastSeenAt = "2026-08-08T00:00:00.000Z",
            ExtensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["alias"] = JsonSerializer.SerializeToElement("activity-test"),
            },
        };
        return new(
            new BindingStoreDocument
            {
                OwnerOpenId = users.Keys.FirstOrDefault(),
                Users = users,
            },
            new SessionStoreDocument
            {
                Sessions = new Dictionary<string, SessionStoreRecord>(StringComparer.Ordinal)
                {
                    [SessionId] = session,
                },
            },
            new RouteStoreDocument(),
            new ApprovalStoreDocument(),
            new SettingsStoreDocument { NotifyActivity = notifyActivity },
            new ControlTokenStoreDocument());
    }

    private static string? ExtensionString(ExtensibleStoreObject value, string name) =>
        value.ExtensionData is not null &&
        value.ExtensionData.TryGetValue(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string CardJson(FeishuCardView card) => card.Content.ToJsonString(
        new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource(
            timeout ?? TimeSpan.FromSeconds(3));
        while (!condition())
        {
            await Task.Delay(10, cancellation.Token);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            ActiveRuntimeActivityCoordinator coordinator,
            RecordingStore store,
            RecordingGateway gateway)
        {
            Coordinator = coordinator;
            Store = store;
            Gateway = gateway;
        }

        public ActiveRuntimeActivityCoordinator Coordinator { get; }
        public RecordingStore Store { get; }
        public RecordingGateway Gateway { get; }

        public static async Task<Fixture> CreateAsync(
            bool notifyActivity = true,
            IReadOnlyList<string>? chats = null,
            RecordingStore? store = null,
            RecordingGateway? gateway = null,
            TimeSpan? flushInterval = null,
            TimeSpan? retryInterval = null)
        {
            store ??= new RecordingStore(StoreSnapshot(
                notifyActivity,
                chats ?? ["chat-1"]));
            gateway ??= new RecordingGateway();
            var options = new BridgeHostOptions(
                Path.GetTempPath(),
                IPAddress.Loopback,
                0,
                BridgeOwnershipMode.Active,
                "activity-test");
            var coordinator = new ActiveRuntimeActivityCoordinator(
                options,
                store,
                gateway,
                new FeishuCardRenderer(),
                flushInterval: flushInterval ?? TimeSpan.FromMilliseconds(30),
                retryInterval: retryInterval ?? TimeSpan.FromMilliseconds(30));
            await coordinator.StartAsync(CancellationToken.None);
            return new(coordinator, store, gateway);
        }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.StopAsync(CancellationToken.None);
            Coordinator.Dispose();
        }
    }

    private sealed class RecordingStore(NodeStoreSnapshot initial) :
        IBridgeProductionStoreOwner
    {
        private readonly object sync = new();
        private NodeStoreSnapshot current = initial;

        public int Updates { get; private set; }

        public NodeStoreSnapshot Current
        {
            get
            {
                lock (sync)
                {
                    return current;
                }
            }
        }

        public BridgeProductionStoreSnapshot Snapshot => new(
            BridgeProductionStoreState.Open,
            Current,
            6);

        public ValueTask OpenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<NodeStoreSnapshot> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Current);
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask UpdateAsync(
            Func<NodeStoreSnapshot, NodeStoreSnapshot> update,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                current = update(current);
                Updates++;
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public void SetSessionStatus(string status)
        {
            lock (sync)
            {
                var session = current.Sessions.Sessions[SessionId];
                var sessions = new Dictionary<string, SessionStoreRecord>(
                    current.Sessions.Sessions,
                    StringComparer.Ordinal)
                {
                    [SessionId] = new()
                    {
                        SessionId = session.SessionId,
                        ShortId = session.ShortId,
                        ProjectName = session.ProjectName,
                        Cwd = session.Cwd,
                        Runtime = session.Runtime,
                        Status = status,
                        OpenedAt = session.OpenedAt,
                        LastSeenAt = session.LastSeenAt,
                        ExtensionData = session.ExtensionData,
                    },
                };
                current = current with
                {
                    Sessions = new SessionStoreDocument
                    {
                        Sessions = sessions,
                        ExtensionData = current.Sessions.ExtensionData,
                    },
                };
            }
        }
    }

    private sealed class RecordingGateway : IFeishuGateway
    {
        private readonly object sync = new();
        private readonly Dictionary<string, string> messageIds =
            new(StringComparer.Ordinal);
        private readonly List<SendAttempt> sends = [];
        private readonly List<PatchAttempt> patches = [];

        public HashSet<string> FailChats { get; } = new(StringComparer.Ordinal);

        public List<string> Operations { get; } = [];

        public IReadOnlyList<SendAttempt> Attempts
        {
            get { lock (sync) return sends.ToArray(); }
        }

        public IReadOnlyList<SendAttempt> Sends => Attempts;

        public IReadOnlyList<PatchAttempt> Patches
        {
            get { lock (sync) return patches.ToArray(); }
        }

        public Task<string> SendCardAsync(
            string chatId,
            FeishuCardView card,
            string? idempotencyKey = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                throw new InvalidOperationException("缺少活动幂等键。");
            }
            lock (sync)
            {
                var messageId = NextMessageId(idempotencyKey);
                sends.Add(new(chatId, idempotencyKey, card, messageId));
                Operations.Add($"send:{CardJson(card)}");
                if (FailChats.Contains(chatId))
                {
                    throw new InvalidOperationException("测试发送失败。");
                }
                return Task.FromResult(messageIds[idempotencyKey]);
            }
        }

        public Task PatchCardAsync(
            string messageId,
            FeishuCardView card,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                patches.Add(new(messageId, card));
                Operations.Add($"patch:{CardJson(card)}");
            }
            return Task.CompletedTask;
        }

        private string NextMessageId(string key)
        {
            if (!messageIds.TryGetValue(key, out var messageId))
            {
                messageId = $"activity-message-{messageIds.Count + 1}";
                messageIds[key] = messageId;
            }
            return messageId;
        }

        public Task<string> SendTextAsync(string chatId, string text,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("活动协调器不应发送文本。");

        public Task<string> ReplyTextAsync(string messageId, string text,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("活动协调器不应回复文本。");

        public Task<FeishuSessionGroup> CreateSessionGroupAsync(string ownerOpenId,
            string name, string description, CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("活动协调器不应创建群聊。");

        public Task UpdateSessionGroupNameAsync(string chatId, string name,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("活动协调器不应更新群聊。");

        public Task DeleteSessionGroupAsync(string chatId,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("活动协调器不应删除群聊。");

        public Task<long> DownloadMessageResourceAsync(string messageId, string fileKey,
            string resourceType, string destinationPath, long maxBytes,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("活动协调器不应下载资源。");

        public Task<string> SendLocalFileAsync(string chatId, string filePath,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("活动协调器不应发送文件。");
    }

    private sealed record SendAttempt(
        string ChatId,
        string IdempotencyKey,
        FeishuCardView Card,
        string MessageId);

    private sealed record PatchAttempt(string MessageId, FeishuCardView Card);

    private sealed class RecordingStateSink : IBridgeActiveRuntimeStateSink
    {
        public List<string> Events { get; } = [];

        public BridgeBusinessStateSnapshot Snapshot =>
            BridgeBusinessStateSnapshot.NotInitialized;

        public Task HandleAsync(
            RuntimeEventEnvelope runtimeEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(runtimeEvent.EventType);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCommandGateway : IBridgeRuntimeCommandGateway
    {
        public bool IsReady(string runtime, RuntimeSession session) => false;

        public Task DispatchAsync(
            RuntimeCommandEnvelope command,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("完成通知不应派发 Runtime 命令。");
    }
}
