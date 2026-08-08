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
    public async Task ExplicitRetryClearsPersistedErrorAndCreatesTheGroup()
    {
        var store = new RecordingStoreOwner(Snapshot(
            ownerOpenId: "owner",
            Session(
                "session-retry",
                Origin,
                extensions: new()
                {
                    ["feishuChatError"] =
                        JsonSerializer.SerializeToElement("old permission error"),
                    ["feishuChatErrorAt"] =
                        JsonSerializer.SerializeToElement(Origin.ToString("O")),
                })));
        var gateway = new RecordingGateway
        {
            CreateError = new HttpRequestException("missing create chat permission"),
        };
        var (state, coordinator) = Owners(store, gateway);
        await state.StartAsync(CancellationToken.None);
        await coordinator.StartAsync(CancellationToken.None);
        Assert.AreEqual(0, gateway.CreateAttempts);

        gateway.CreateError = null;
        var result = await coordinator.RetryAsync("session-retry");

        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(result.AlreadyConnected);
        Assert.AreEqual("chat-created-1", result.ChatId);
        Assert.AreEqual("Codex｜project", result.ChatName);
        Assert.IsNull(
            ExtensionString(store.Current, "session-retry", "feishuChatError"));
        Assert.IsNull(
            ExtensionString(store.Current, "session-retry", "feishuChatErrorAt"));
        Assert.AreEqual(1, gateway.CreateAttempts);

        await coordinator.StopAsync(CancellationToken.None);
        coordinator.Dispose();
    }

    [TestMethod]
    public async Task RetryOfAnAlreadyConnectedGroupIsIdempotent()
    {
        var store = new RecordingStoreOwner(Snapshot(
            ownerOpenId: "owner",
            Session(
                "session-connected",
                Origin,
                extensions: new()
                {
                    ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-connected"),
                    ["feishuChatName"] = JsonSerializer.SerializeToElement("Codex｜project"),
                    ["feishuChatOrdinal"] = JsonSerializer.SerializeToElement(1),
                })));
        var gateway = new RecordingGateway();
        var (state, coordinator) = Owners(store, gateway);
        await state.StartAsync(CancellationToken.None);
        await coordinator.StartAsync(CancellationToken.None);

        var result = await coordinator.RetryAsync("session-connected");

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.AlreadyConnected);
        Assert.AreEqual("chat-connected", result.ChatId);
        Assert.AreEqual("Codex｜project", result.ChatName);
        Assert.AreEqual(0, gateway.CreateAttempts);

        await coordinator.StopAsync(CancellationToken.None);
        coordinator.Dispose();
    }

    [TestMethod]
    public async Task ExplicitRetryPersistsAndReturnsTheLatestFailure()
    {
        var store = new RecordingStoreOwner(Snapshot(
            ownerOpenId: "owner",
            Session(
                "session-retry-failed",
                Origin,
                extensions: new()
                {
                    ["feishuChatError"] =
                        JsonSerializer.SerializeToElement("old permission error"),
                    ["feishuChatErrorAt"] =
                        JsonSerializer.SerializeToElement(Origin.AddHours(-1).ToString("O")),
                })));
        var gateway = new RecordingGateway
        {
            CreateError = new HttpRequestException("new create chat permission error"),
        };
        var (state, coordinator) = Owners(store, gateway);
        await state.StartAsync(CancellationToken.None);
        await coordinator.StartAsync(CancellationToken.None);

        var result = await coordinator.RetryAsync("session-retry-failed");

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(result.AlreadyConnected);
        StringAssert.Contains(result.Error!, "new create chat permission error");
        Assert.AreEqual(
            result.Error,
            ExtensionString(
                store.Current,
                "session-retry-failed",
                "feishuChatError"));
        Assert.AreEqual(
            Origin.ToString("O"),
            ExtensionString(
                store.Current,
                "session-retry-failed",
                "feishuChatErrorAt"));
        Assert.AreEqual(1, gateway.CreateAttempts);

        await coordinator.StopAsync(CancellationToken.None);
        coordinator.Dispose();
    }

    [TestMethod]
    public async Task ConcurrentExplicitRetriesShareOneRemoteCreate()
    {
        var store = new RecordingStoreOwner(Snapshot(
            ownerOpenId: "owner",
            Session(
                "session-retry-concurrent",
                Origin,
                extensions: new()
                {
                    ["feishuChatError"] =
                        JsonSerializer.SerializeToElement("old permission error"),
                    ["feishuChatErrorAt"] =
                        JsonSerializer.SerializeToElement(Origin.ToString("O")),
                })));
        var gateway = new RecordingGateway
        {
            CreateRelease = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var (state, coordinator) = Owners(store, gateway);
        await state.StartAsync(CancellationToken.None);
        await coordinator.StartAsync(CancellationToken.None);

        var first = coordinator.RetryAsync("session-retry-concurrent").AsTask();
        await gateway.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = coordinator.RetryAsync("session-retry-concurrent").AsTask();
        Assert.AreEqual(1, gateway.CreateAttempts);

        gateway.CreateRelease.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.IsTrue(results.All(result => result.Succeeded));
        Assert.AreEqual(1, gateway.CreateAttempts);
        Assert.AreEqual(results[0].ChatId, results[1].ChatId);
        Assert.AreEqual(1, gateway.Welcome.Count);

        await coordinator.StopAsync(CancellationToken.None);
        coordinator.Dispose();
    }

    [TestMethod]
    public async Task BindingWonDuringErrorClearMakesRetryIdempotentlyConnected()
    {
        var store = new RecordingStoreOwner(Snapshot(
            ownerOpenId: "owner",
            Session(
                "session-retry-race",
                Origin,
                extensions: new()
                {
                    ["feishuChatOrdinal"] = JsonSerializer.SerializeToElement(1),
                    ["feishuChatError"] =
                        JsonSerializer.SerializeToElement("old permission error"),
                    ["feishuChatErrorAt"] =
                        JsonSerializer.SerializeToElement(Origin.ToString("O")),
                })));
        store.BeforeUpdate = current =>
            NodeStoreBusinessStateMerger.PatchSessionExtensions(
                current,
                "session-retry-race",
                new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                {
                    ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-winner"),
                    ["feishuChatName"] = JsonSerializer.SerializeToElement("winner"),
                });
        var gateway = new RecordingGateway();
        var (state, coordinator) = Owners(store, gateway);
        await state.StartAsync(CancellationToken.None);
        await coordinator.StartAsync(CancellationToken.None);

        var result = await coordinator.RetryAsync("session-retry-race");

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.AlreadyConnected);
        Assert.AreEqual("chat-winner", result.ChatId);
        Assert.AreEqual("winner", result.ChatName);
        Assert.AreEqual(0, gateway.CreateAttempts);

        await coordinator.StopAsync(CancellationToken.None);
        coordinator.Dispose();
    }

    [TestMethod]
    public async Task StartupDeletesOnlyGroupsInactiveForSevenDays()
    {
        var oldAt = Origin.AddDays(-8);
        var recentCreatedAt = Origin.AddDays(-2);
        var store = new RecordingStoreOwner(Snapshot(
            ownerOpenId: "owner",
            Session(
                "session-cleanup-old",
                oldAt,
                extensions: new()
                {
                    ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-old"),
                    ["feishuChatName"] = JsonSerializer.SerializeToElement("old"),
                    ["feishuChatCreatedAt"] =
                        JsonSerializer.SerializeToElement(oldAt.ToString("O")),
                    ["feishuChatOrdinal"] = JsonSerializer.SerializeToElement(1),
                    ["feishuChatError"] = JsonSerializer.SerializeToElement("old error"),
                    ["feishuChatErrorAt"] =
                        JsonSerializer.SerializeToElement(oldAt.ToString("O")),
                },
                status: SessionStatuses.Ended),
            Session(
                "session-cleanup-recent",
                oldAt,
                extensions: new()
                {
                    ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-recent"),
                    ["feishuChatName"] = JsonSerializer.SerializeToElement("recent"),
                    ["feishuChatCreatedAt"] =
                        JsonSerializer.SerializeToElement(recentCreatedAt.ToString("O")),
                    ["feishuChatOrdinal"] = JsonSerializer.SerializeToElement(2),
                },
                status: SessionStatuses.Ended)));
        var gateway = new RecordingGateway();
        var (state, coordinator) = Owners(store, gateway);
        await state.StartAsync(CancellationToken.None);

        await coordinator.StartAsync(CancellationToken.None);
        var cleanupLoop = coordinator.Completion;

        CollectionAssert.AreEqual(new[] { "chat-old" }, gateway.Deleted.ToArray());
        Assert.IsNull(ExtensionString(
            store.Current,
            "session-cleanup-old",
            "feishuChatId"));
        Assert.IsNull(ExtensionString(
            store.Current,
            "session-cleanup-old",
            "feishuChatError"));
        Assert.AreEqual(1, Ordinal(store.Current, "session-cleanup-old"));
        Assert.AreEqual(
            "chat-recent",
            ExtensionString(
                store.Current,
                "session-cleanup-recent",
                "feishuChatId"));
        Assert.IsNotNull(cleanupLoop);
        Assert.IsFalse(cleanupLoop.IsCompleted);

        await coordinator.StopAsync(CancellationToken.None);
        Assert.IsTrue(cleanupLoop.IsCompleted);
        coordinator.Dispose();
    }

    [TestMethod]
    public async Task DeleteFailureLeavesTheInactiveBindingForRetry()
    {
        var store = new RecordingStoreOwner(Snapshot(ownerOpenId: "owner"));
        var gateway = new RecordingGateway
        {
            DeleteError = new HttpRequestException("missing delete chat permission"),
        };
        var (state, coordinator) = Owners(store, gateway);
        await state.StartAsync(CancellationToken.None);
        await coordinator.StartAsync(CancellationToken.None);
        store.Replace(Snapshot(
            ownerOpenId: "owner",
            Session(
                "session-cleanup-failed",
                Origin.AddDays(-8),
                extensions: new()
                {
                    ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-failed"),
                    ["feishuChatName"] = JsonSerializer.SerializeToElement("failed"),
                    ["feishuChatCreatedAt"] =
                        JsonSerializer.SerializeToElement(Origin.AddDays(-8).ToString("O")),
                },
                status: SessionStatuses.Ended)));

        var result = await coordinator.CleanupAsync(Origin);

        Assert.AreEqual(0, result.Deleted);
        Assert.AreEqual(1, result.Failed);
        CollectionAssert.AreEqual(
            new[] { "chat-failed" },
            gateway.DeleteAttempts.ToArray());
        Assert.AreEqual(0, gateway.Deleted.Count);
        Assert.AreEqual(
            "chat-failed",
            ExtensionString(
                store.Current,
                "session-cleanup-failed",
                "feishuChatId"));

        await coordinator.StopAsync(CancellationToken.None);
        coordinator.Dispose();
    }

    [TestMethod]
    public async Task StoreClearFailureKeepsTheDeletedBindingVisibleAsFailed()
    {
        var oldAt = Origin.AddDays(-8);
        var store = new RecordingStoreOwner(Snapshot(ownerOpenId: "owner"));
        var gateway = new RecordingGateway();
        var (state, coordinator) = Owners(store, gateway);
        await state.StartAsync(CancellationToken.None);
        await coordinator.StartAsync(CancellationToken.None);
        store.Replace(Snapshot(
            ownerOpenId: "owner",
            Session(
                "session-cleanup-store-failed",
                oldAt,
                extensions: new()
                {
                    ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-store-failed"),
                    ["feishuChatName"] = JsonSerializer.SerializeToElement("store-failed"),
                    ["feishuChatCreatedAt"] =
                        JsonSerializer.SerializeToElement(oldAt.ToString("O")),
                },
                status: SessionStatuses.Ended)));
        store.UpdateError = new IOException("session group clear write failed");

        var result = await coordinator.CleanupAsync(Origin);

        Assert.AreEqual(0, result.Deleted);
        Assert.AreEqual(1, result.Failed);
        CollectionAssert.AreEqual(
            new[] { "chat-store-failed" },
            gateway.Deleted.ToArray());
        Assert.AreEqual(
            "chat-store-failed",
            ExtensionString(
                store.Current,
                "session-cleanup-store-failed",
                "feishuChatId"));

        await coordinator.StopAsync(CancellationToken.None);
        coordinator.Dispose();
    }

    [TestMethod]
    public async Task ReplacementBindingSurvivesAnOldGroupDelete()
    {
        var oldAt = Origin.AddDays(-8);
        var store = new RecordingStoreOwner(Snapshot(ownerOpenId: "owner"));
        var gateway = new RecordingGateway();
        gateway.OnDeleted = _ => store.Replace(Snapshot(
            ownerOpenId: "owner",
            Session(
                "session-cleanup-race",
                Origin,
                extensions: new()
                {
                    ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-new"),
                    ["feishuChatName"] = JsonSerializer.SerializeToElement("new"),
                    ["feishuChatCreatedAt"] =
                        JsonSerializer.SerializeToElement(Origin.ToString("O")),
                },
                status: SessionStatuses.Ended)));
        var (state, coordinator) = Owners(store, gateway);
        await state.StartAsync(CancellationToken.None);
        await coordinator.StartAsync(CancellationToken.None);
        store.Replace(Snapshot(
            ownerOpenId: "owner",
            Session(
                "session-cleanup-race",
                oldAt,
                extensions: new()
                {
                    ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-old"),
                    ["feishuChatName"] = JsonSerializer.SerializeToElement("old"),
                    ["feishuChatCreatedAt"] =
                        JsonSerializer.SerializeToElement(oldAt.ToString("O")),
                },
                status: SessionStatuses.Ended)));

        var result = await coordinator.CleanupAsync(Origin);

        Assert.AreEqual(1, result.Deleted);
        Assert.AreEqual(0, result.Failed);
        CollectionAssert.AreEqual(new[] { "chat-old" }, gateway.Deleted.ToArray());
        Assert.AreEqual(
            "chat-new",
            ExtensionString(
                store.Current,
                "session-cleanup-race",
                "feishuChatId"));
        Assert.AreEqual(
            "new",
            ExtensionString(
                store.Current,
                "session-cleanup-race",
                "feishuChatName"));

        await coordinator.StopAsync(CancellationToken.None);
        coordinator.Dispose();
    }

    [TestMethod]
    public async Task ActivityRefreshedDuringCleanupSkipsTheLaterCandidate()
    {
        var firstAt = Origin.AddDays(-9);
        var secondAt = Origin.AddDays(-8);
        var store = new RecordingStoreOwner(Snapshot(ownerOpenId: "owner"));
        var gateway = new RecordingGateway();
        gateway.OnDeleted = chatId =>
        {
            if (chatId != "chat-first")
            {
                return;
            }
            store.Replace(Snapshot(
                ownerOpenId: "owner",
                Session(
                    "session-cleanup-first",
                    firstAt,
                    extensions: new()
                    {
                        ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-first"),
                        ["feishuChatName"] = JsonSerializer.SerializeToElement("first"),
                        ["feishuChatCreatedAt"] =
                            JsonSerializer.SerializeToElement(firstAt.ToString("O")),
                    },
                    status: SessionStatuses.Ended),
                Session(
                    "session-cleanup-second",
                    Origin,
                    extensions: new()
                    {
                        ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-second"),
                        ["feishuChatName"] = JsonSerializer.SerializeToElement("second"),
                        ["feishuChatCreatedAt"] =
                            JsonSerializer.SerializeToElement(secondAt.ToString("O")),
                    },
                    status: SessionStatuses.Ended)));
        };
        var (state, coordinator) = Owners(store, gateway);
        await state.StartAsync(CancellationToken.None);
        await coordinator.StartAsync(CancellationToken.None);
        store.Replace(Snapshot(
            ownerOpenId: "owner",
            Session(
                "session-cleanup-first",
                firstAt,
                extensions: new()
                {
                    ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-first"),
                    ["feishuChatName"] = JsonSerializer.SerializeToElement("first"),
                    ["feishuChatCreatedAt"] =
                        JsonSerializer.SerializeToElement(firstAt.ToString("O")),
                },
                status: SessionStatuses.Ended),
            Session(
                "session-cleanup-second",
                secondAt,
                extensions: new()
                {
                    ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-second"),
                    ["feishuChatName"] = JsonSerializer.SerializeToElement("second"),
                    ["feishuChatCreatedAt"] =
                        JsonSerializer.SerializeToElement(secondAt.ToString("O")),
                },
                status: SessionStatuses.Ended)));

        var result = await coordinator.CleanupAsync(Origin);

        Assert.AreEqual(1, result.Deleted);
        Assert.AreEqual(0, result.Failed);
        CollectionAssert.AreEqual(new[] { "chat-first" }, gateway.Deleted.ToArray());
        Assert.AreEqual(
            "chat-second",
            ExtensionString(
                store.Current,
                "session-cleanup-second",
                "feishuChatId"));

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
        string runtime = RuntimeNames.Codex,
        string status = SessionStatuses.Waiting)
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
            Status = status,
            Runtime = runtime,
            OpenedAt = openedAt.ToString("O"),
            LastSeenAt = openedAt.ToString("O"),
            EndedAt = status == SessionStatuses.Ended
                ? openedAt.ToString("O")
                : null,
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

        public Func<NodeStoreSnapshot, NodeStoreSnapshot>? BeforeUpdate { get; set; }
        public Exception? UpdateError { get; set; }

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
            if (UpdateError is not null)
            {
                return ValueTask.FromException(UpdateError);
            }
            lock (sync)
            {
                var beforeUpdate = BeforeUpdate;
                BeforeUpdate = null;
                if (beforeUpdate is not null)
                {
                    current = beforeUpdate(current);
                }
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
        public List<string> DeleteAttempts { get; } = [];
        public List<string> Deleted { get; } = [];
        public List<(string ChatId, string Text)> Welcome { get; } = [];
        public Exception? CreateError { get; set; }
        public Exception? DeleteError { get; set; }
        public TaskCompletionSource CreateStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? CreateRelease { get; set; }
        public Action<FeishuSessionGroup>? OnCreated { get; set; }
        public Action<string>? OnDeleted { get; set; }
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
            DeleteAttempts.Add(chatId);
            if (DeleteError is not null)
            {
                throw DeleteError;
            }
            Deleted.Add(chatId);
            OnDeleted?.Invoke(chatId);
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
