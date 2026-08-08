using System.Net;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActiveSessionGroupCoordinatorTests
{
    private static readonly DateTimeOffset Origin =
        DateTimeOffset.Parse("2026-08-08T08:00:00.0000000+00:00");

    [TestMethod]
    public async Task StartupCreatesStableNumberedGroupsAndRenamesExistingBinding()
    {
        var store = new RecordingStoreOwner(Snapshot(
            ownerOpenId: "owner",
            Session(
                "session-one",
                Origin,
                extensions: new()
                {
                    ["futureSession"] = JsonSerializer.SerializeToElement("keep"),
                }),
            Session("session-two", Origin.AddMinutes(1)),
            Session(
                "session-three",
                Origin.AddMinutes(2),
                extensions: new()
                {
                    ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-existing"),
                    ["feishuChatName"] = JsonSerializer.SerializeToElement("old-name"),
                }),
            Session(
                "session-claude",
                Origin.AddMinutes(3),
                runtime: RuntimeNames.ClaudeCode)));
        var gateway = new RecordingGateway();
        var (state, coordinator) = Owners(store, gateway);
        await state.StartAsync(CancellationToken.None);

        await coordinator.StartAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[]
            {
                "Codex｜project",
                "Codex｜project（2）",
                "Claude｜project",
            },
            gateway.Created.Select(item => item.Name).ToArray());
        CollectionAssert.AreEqual(
            new[] { ("chat-existing", "Codex｜project（3）") },
            gateway.Renamed.ToArray());
        Assert.AreEqual(3, gateway.Welcome.Count);
        Assert.AreEqual(1, Ordinal(store.Current, "session-one"));
        Assert.AreEqual(2, Ordinal(store.Current, "session-two"));
        Assert.AreEqual(3, Ordinal(store.Current, "session-three"));
        Assert.AreEqual(1, Ordinal(store.Current, "session-claude"));
        Assert.AreEqual(
            "keep",
            ExtensionString(store.Current, "session-one", "futureSession"));
        Assert.AreEqual(
            "Codex｜project（3）",
            ExtensionString(store.Current, "session-three", "feishuChatName"));

        var firstChats = await coordinator.NotificationChatsAsync("session-one");
        CollectionAssert.AreEqual(
            new[] { ExtensionString(store.Current, "session-one", "feishuChatId")! },
            firstChats.ToArray());

        await coordinator.StopAsync(CancellationToken.None);
        coordinator.Dispose();
    }

    [TestMethod]
    public async Task PersistedCreateFailureFallsBackToBindingsWithoutAutomaticRetry()
    {
        var store = new RecordingStoreOwner(Snapshot(
            ownerOpenId: "owner",
            Session("session-failed", Origin)));
        var gateway = new RecordingGateway
        {
            CreateError = new HttpRequestException("missing create chat permission"),
        };
        var (state, coordinator) = Owners(store, gateway);
        await state.StartAsync(CancellationToken.None);

        await coordinator.StartAsync(CancellationToken.None);
        var chats = await coordinator.NotificationChatsAsync("session-failed");

        Assert.AreEqual(1, gateway.CreateAttempts);
        CollectionAssert.AreEqual(new[] { "chat-owner" }, chats.ToArray());
        StringAssert.Contains(
            ExtensionString(store.Current, "session-failed", "feishuChatError")!,
            "permission");
        Assert.AreEqual(
            Origin.ToString("O"),
            ExtensionString(
                store.Current,
                "session-failed",
                "feishuChatErrorAt"));

        await coordinator.StopAsync(CancellationToken.None);
        coordinator.Dispose();
    }

    [TestMethod]
    public async Task ConcurrentEnsureCallsShareOneRemoteCreate()
    {
        var store = new RecordingStoreOwner(Snapshot(ownerOpenId: "owner"));
        var gateway = new RecordingGateway
        {
            CreateRelease = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var (state, coordinator) = Owners(store, gateway);
        await state.StartAsync(CancellationToken.None);
        await coordinator.StartAsync(CancellationToken.None);
        store.Replace(Snapshot(
            ownerOpenId: "owner",
            Session("session-concurrent", Origin)));

        var first = coordinator.EnsureAsync("session-concurrent").AsTask();
        await gateway.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = coordinator.EnsureAsync("session-concurrent").AsTask();
        Assert.AreEqual(1, gateway.CreateAttempts);

        gateway.CreateRelease.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.AreEqual(1, gateway.CreateAttempts);
        Assert.AreEqual(
            ExtensionString(results[0]!, "feishuChatId"),
            ExtensionString(results[1]!, "feishuChatId"));
        Assert.AreEqual(1, gateway.Welcome.Count);

        await coordinator.StopAsync(CancellationToken.None);
        coordinator.Dispose();
    }

    [TestMethod]
    public async Task ReplacedBindingDeletesTheJustCreatedRemoteGroup()
    {
        var store = new RecordingStoreOwner(Snapshot(
            ownerOpenId: "owner",
            Session("session-race", Origin)));
        var gateway = new RecordingGateway();
        gateway.OnCreated = group => store.Replace(
            NodeStoreBusinessStateMerger.PatchSessionExtensions(
                store.Current,
                "session-race",
                new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                {
                    ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-winner"),
                    ["feishuChatName"] = JsonSerializer.SerializeToElement("winner"),
                }));
        var (state, coordinator) = Owners(store, gateway);
        await state.StartAsync(CancellationToken.None);

        await coordinator.StartAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "chat-created-1" }, gateway.Deleted.ToArray());
        Assert.AreEqual(
            "chat-winner",
            ExtensionString(store.Current, "session-race", "feishuChatId"));
        Assert.AreEqual(0, gateway.Welcome.Count);

        await coordinator.StopAsync(CancellationToken.None);
        coordinator.Dispose();
    }

    [TestMethod]
    public async Task AliasChangedDuringCreateIsReconciledAfterDurableBinding()
    {
        var store = new RecordingStoreOwner(Snapshot(
            ownerOpenId: "owner",
            Session("session-alias-race", Origin)));
        var gateway = new RecordingGateway();
        gateway.OnCreated = _ => store.Replace(
            NodeStoreBusinessStateMerger.PatchSessionExtensions(
                store.Current,
                "session-alias-race",
                new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                {
                    ["alias"] = JsonSerializer.SerializeToElement("新名称"),
                }));
        var (state, coordinator) = Owners(store, gateway);
        await state.StartAsync(CancellationToken.None);

        await coordinator.StartAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { ("chat-created-1", "Codex｜新名称") },
            gateway.Renamed.ToArray());
        Assert.AreEqual(
            "Codex｜新名称",
            ExtensionString(
                store.Current,
                "session-alias-race",
                "feishuChatName"));
        StringAssert.Contains(gateway.Welcome.Single().Text, "@新名称");

        await coordinator.StopAsync(CancellationToken.None);
        coordinator.Dispose();
    }

    [TestMethod]
    public async Task OwnerChangedDuringCreateRejectsBindingAndDeletesRemoteGroup()
    {
        var store = new RecordingStoreOwner(Snapshot(
            ownerOpenId: "owner",
            Session("session-owner-race", Origin)));
        var gateway = new RecordingGateway();
        gateway.OnCreated = _ => store.Replace(store.Current with
        {
            Bindings = new BindingStoreDocument
            {
                OwnerOpenId = "owner-new",
                Users = new Dictionary<string, BindingStoreRecord>(StringComparer.Ordinal)
                {
                    ["owner-new"] = new()
                    {
                        OpenId = "owner-new",
                        ChatId = "chat-owner-new",
                        ChatType = "p2p",
                        BoundAt = Origin.AddMinutes(1).ToString("O"),
                    },
                },
            },
        });
        var (state, coordinator) = Owners(store, gateway);
        await state.StartAsync(CancellationToken.None);

        await coordinator.StartAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "chat-created-1" }, gateway.Deleted.ToArray());
        Assert.IsNull(ExtensionString(
            store.Current,
            "session-owner-race",
            "feishuChatId"));
        Assert.AreEqual(0, gateway.Welcome.Count);

        await coordinator.StopAsync(CancellationToken.None);
        coordinator.Dispose();
    }

    private static (
        ActivePersistentBusinessStateOwner State,
        ActiveSessionGroupCoordinator Coordinator) Owners(
            RecordingStoreOwner store,
            RecordingGateway gateway)
    {
        var options = new BridgeHostOptions(
            Path.GetTempPath(),
            IPAddress.Loopback,
            0,
            BridgeOwnershipMode.Active,
            "session-group-test");
        var clock = new FixedTimeProvider(Origin);
        var state = new ActivePersistentBusinessStateOwner(options, store, clock);
        return (
            state,
            new ActiveSessionGroupCoordinator(
                options,
                store,
                state,
                gateway,
                clock,
                TimeSpan.FromDays(7)));
    }

    private static NodeStoreSnapshot Snapshot(
        string? ownerOpenId,
        params SessionStoreRecord[] sessions) => new(
        new BindingStoreDocument
        {
            OwnerOpenId = ownerOpenId,
            Users = ownerOpenId is null
                ? []
                : new Dictionary<string, BindingStoreRecord>(StringComparer.Ordinal)
                {
                    [ownerOpenId] = new()
                    {
                        OpenId = ownerOpenId,
                        ChatId = "chat-owner",
                        ChatType = "p2p",
                        BoundAt = Origin.ToString("O"),
                    },
                },
        },
        new SessionStoreDocument
        {
            Sessions = sessions.ToDictionary(
                session => session.SessionId,
                StringComparer.Ordinal),
        },
        new RouteStoreDocument(),
        new ApprovalStoreDocument(),
        new SettingsStoreDocument(),
        new ControlTokenStoreDocument());

    private static SessionStoreRecord Session(
        string sessionId,
        DateTimeOffset openedAt,
        Dictionary<string, JsonElement>? extensions = null,
        string runtime = RuntimeNames.Codex)
    {
        var values = extensions is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(extensions, StringComparer.Ordinal);
        values["managedByAssistant"] = JsonSerializer.SerializeToElement(true);
        values["historyEligible"] = JsonSerializer.SerializeToElement(true);
        return new()
        {
            SessionId = sessionId,
            ShortId = sessionId[^Math.Min(8, sessionId.Length)..],
            Cwd = "K:/workspace/project",
            ProjectName = "project",
            Status = SessionStatuses.Waiting,
            Runtime = runtime,
            OpenedAt = openedAt.ToString("O"),
            LastSeenAt = openedAt.ToString("O"),
            ExtensionData = values,
        };
    }

    private static int Ordinal(NodeStoreSnapshot store, string sessionId) =>
        store.Sessions.Sessions[sessionId]
            .ExtensionData!["feishuChatOrdinal"]
            .GetInt32();

    private static string? ExtensionString(
        NodeStoreSnapshot store,
        string sessionId,
        string name) =>
        ExtensionString(store.Sessions.Sessions[sessionId], name);

    private static string? ExtensionString(
        SessionStoreRecord session,
        string name) =>
        session.ExtensionData is not null &&
        session.ExtensionData.TryGetValue(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed class RecordingStoreOwner(NodeStoreSnapshot store)
        : IBridgeProductionStoreOwner
    {
        private readonly object sync = new();
        private NodeStoreSnapshot current = store;

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
            null,
            0);

        public void Replace(NodeStoreSnapshot value)
        {
            lock (sync)
            {
                current = value;
            }
        }

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
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class RecordingGateway : IFeishuGateway
    {
        public List<(string OwnerOpenId, string Name, string Description)> Created { get; } = [];
        public List<(string ChatId, string Name)> Renamed { get; } = [];
        public List<string> Deleted { get; } = [];
        public List<(string ChatId, string Text)> Welcome { get; } = [];
        public Exception? CreateError { get; set; }
        public TaskCompletionSource CreateStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? CreateRelease { get; set; }
        public Action<FeishuSessionGroup>? OnCreated { get; set; }
        public int CreateAttempts { get; private set; }

        public Task<string> SendTextAsync(
            string chatId,
            string text,
            CancellationToken cancellationToken = default)
        {
            Welcome.Add((chatId, text));
            return Task.FromResult($"welcome-{Welcome.Count}");
        }

        public Task<string> ReplyTextAsync(
            string messageId,
            string text,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("会话群测试不应回复消息。");

        public Task<string> SendCardAsync(
            string chatId,
            FeishuCardView card,
            string? idempotencyKey = null,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("会话群测试不应发送卡片。");

        public Task PatchCardAsync(
            string messageId,
            FeishuCardView card,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("会话群测试不应更新卡片。");

        public async Task<FeishuSessionGroup> CreateSessionGroupAsync(
            string ownerOpenId,
            string name,
            string description,
            CancellationToken cancellationToken = default)
        {
            CreateAttempts++;
            CreateStarted.TrySetResult();
            if (CreateRelease is not null)
            {
                await CreateRelease.Task.WaitAsync(cancellationToken);
            }
            if (CreateError is not null)
            {
                throw CreateError;
            }
            Created.Add((ownerOpenId, name, description));
            var group = new FeishuSessionGroup(
                $"chat-created-{CreateAttempts}",
                name);
            OnCreated?.Invoke(group);
            return group;
        }

        public Task UpdateSessionGroupNameAsync(
            string chatId,
            string name,
            CancellationToken cancellationToken = default)
        {
            Renamed.Add((chatId, name));
            return Task.CompletedTask;
        }

        public Task DeleteSessionGroupAsync(
            string chatId,
            CancellationToken cancellationToken = default)
        {
            Deleted.Add(chatId);
            return Task.CompletedTask;
        }

        public Task<long> DownloadMessageResourceAsync(
            string messageId,
            string fileKey,
            string resourceType,
            string destinationPath,
            long maxBytes,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("会话群测试不应下载附件。");

        public Task<string> SendLocalFileAsync(
            string chatId,
            string filePath,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("会话群测试不应发送文件。");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
