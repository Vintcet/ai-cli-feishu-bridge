using System.Net;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActivePersistentBusinessStateOwnerTests
{
    private static readonly DateTimeOffset Origin =
        DateTimeOffset.Parse("2026-08-06T00:00:00Z");
    private string? directory;

    [TestInitialize]
    public void Initialize() => directory = Path.Combine(
        Path.GetTempPath(),
        $"active-business-state-{Guid.NewGuid():N}");

    [TestCleanup]
    public void Cleanup()
    {
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task StartsAfterStoreAndRecoversPendingApprovalsDurably()
    {
        await WriteStoreAsync(
            SessionStatuses.PendingApproval,
            ApprovalStatuses.Pending,
            includeExtensions: true);
        await using var lease = new ActiveOwnerLeaseAcquirer(Options());
        await lease.AcquireAsync();
        var store = StoreOwner(lease);
        await store.OpenAsync();
        var owner = Owner(store, Origin.AddHours(1));

        await owner.StartAsync(CancellationToken.None);

        Assert.IsTrue(owner.Snapshot.Initialized);
        Assert.AreEqual("production", owner.Snapshot.SourceStatus);
        Assert.AreEqual(
            ApprovalStatuses.Orphaned,
            owner.Snapshot.Approvals.Requests["approval-1"].Status);
        Assert.AreEqual(
            ApprovalResolutions.Local,
            owner.Snapshot.Approvals.Requests["approval-1"].Resolution);
        Assert.AreEqual(
            SessionStatuses.LocalApproval,
            owner.Snapshot.Sessions.Sessions["session-1"].Status);
        Assert.AreEqual(0, owner.Snapshot.Inputs.Requests.Count);
        var reloaded = await new BridgeJsonStoreRepository(directory!).LoadAsync();
        Assert.AreEqual(
            ApprovalStatuses.Orphaned,
            reloaded.Approvals.Requests["approval-1"].Status);
        Assert.AreEqual(
            "keep-session",
            reloaded.Sessions.Sessions["session-1"]
                .ExtensionData!["futureSession"].GetString());
        Assert.AreEqual(
            "keep-root",
            reloaded.Sessions.ExtensionData!["futureRoot"].GetString());

        await store.CloseAsync();
        await lease.ReleaseAsync();
    }

    [TestMethod]
    public async Task RuntimeMutationCommitsStoreBeforePublishingMemorySnapshot()
    {
        await WriteStoreAsync(SessionStatuses.Waiting, approvalStatus: null);
        await using var lease = new ActiveOwnerLeaseAcquirer(Options());
        await lease.AcquireAsync();
        var store = StoreOwner(lease);
        await store.OpenAsync();
        var owner = Owner(store, Origin);
        await owner.StartAsync(CancellationToken.None);

        await owner.HandleAsync(Event(
            "turn-started",
            RuntimeEventTypes.TurnStarted,
            Origin.AddMinutes(2),
            new { turnId = "turn-2" }));

        Assert.AreEqual(1, owner.Snapshot.Revision);
        Assert.AreEqual(
            SessionStatuses.Running,
            owner.Snapshot.Sessions.Sessions["session-1"].Status);
        var reloaded = await new BridgeJsonStoreRepository(directory!).LoadAsync();
        Assert.AreEqual(
            SessionStatuses.Running,
            reloaded.Sessions.Sessions["session-1"].Status);
        Assert.AreEqual("existing-short", reloaded.Sessions.Sessions["session-1"].ShortId);
        Assert.AreEqual("keep-settings", reloaded.Settings.ExtensionData!["future"].GetString());
        var authoritative = owner.Snapshot;
        await ((IBridgeControlBusinessStateSource)owner).RefreshAsync();
        Assert.AreSame(authoritative, owner.Snapshot);

        await store.CloseAsync();
        await lease.ReleaseAsync();
    }

    [TestMethod]
    public async Task SessionStartPersistsManagedBindingInTheSameBusinessCommit()
    {
        await WriteStoreAsync(SessionStatuses.Waiting, approvalStatus: null);
        await using var lease = new ActiveOwnerLeaseAcquirer(Options());
        await lease.AcquireAsync();
        var store = StoreOwner(lease);
        await store.OpenAsync();
        var owner = Owner(store, Origin);
        await owner.StartAsync(CancellationToken.None);

        await owner.HandleAsync(Event(
            "managed-session-started",
            RuntimeEventTypes.SessionStarted,
            Origin.AddMinutes(2),
            new
            {
                model = "gpt-5",
                source = "startup",
                managedTerminalId = "terminal-managed",
                managedTerminalElevated = true,
                managedByAssistant = true,
                historyEligible = true,
            }));

        Assert.AreEqual(1, owner.Snapshot.Revision);
        Assert.AreEqual(
            SessionStatuses.Ready,
            owner.Snapshot.Sessions.Sessions["session-1"].Status);
        var reloaded = await new BridgeJsonStoreRepository(directory!).LoadAsync();
        var extensions = reloaded.Sessions.Sessions["session-1"].ExtensionData!;
        Assert.AreEqual(
            "terminal-managed",
            extensions["managedTerminalId"].GetString());
        Assert.IsTrue(extensions["managedTerminalElevated"].GetBoolean());
        Assert.IsTrue(extensions["managedByAssistant"].GetBoolean());
        Assert.IsTrue(extensions["historyEligible"].GetBoolean());
        Assert.AreEqual("startup", extensions["source"].GetString());

        await store.CloseAsync();
        await lease.ReleaseAsync();
    }

    [TestMethod]
    public async Task OpenCodeSessionStartPersistsAssistantOwnershipMetadata()
    {
        var store = new RecordingStoreOwner(SnapshotFromMemory());
        var owner = Owner(store, Origin);
        await owner.StartAsync(CancellationToken.None);

        await owner.HandleAsync(Event(
            "opencode-session-started",
            RuntimeEventTypes.SessionStarted,
            Origin.AddMinutes(2),
            new { model = "openai/gpt-5" },
            RuntimeNames.OpenCode));

        var session = store.Current.Sessions.Sessions["session-1"];
        Assert.AreEqual(RuntimeNames.OpenCode, session.Runtime);
        Assert.IsTrue(session.ExtensionData!["managedByAssistant"].GetBoolean());
        Assert.IsTrue(session.ExtensionData["historyEligible"].GetBoolean());
        Assert.AreEqual("opencode", session.ExtensionData["source"].GetString());
    }

    [TestMethod]
    public async Task ApprovalRequestPersistsRequiredCompatibilityFields()
    {
        await WriteStoreAsync(SessionStatuses.Waiting, approvalStatus: null);
        await using var lease = new ActiveOwnerLeaseAcquirer(Options());
        await lease.AcquireAsync();
        var store = StoreOwner(lease);
        await store.OpenAsync();
        var owner = Owner(store, Origin);
        await owner.StartAsync(CancellationToken.None);

        await owner.HandleAsync(Event(
            "approval-requested",
            RuntimeEventTypes.ApprovalRequested,
            Origin.AddMinutes(2),
            new
            {
                requestId = "approval-new",
                title = "shell_command",
                description = "echo test",
                expiresAt = Origin.AddMinutes(22).ToString("O"),
            }));

        var reloaded = await new BridgeJsonStoreRepository(directory!).LoadAsync();
        var approval = reloaded.Approvals.Requests["approval-new"];
        Assert.AreEqual("turn-1", approval.TurnId);
        Assert.AreEqual("K:/repo", approval.Cwd);
        Assert.AreEqual("shell_command", approval.ToolName);
        Assert.AreEqual("echo test", approval.ToolPreview);
        Assert.AreEqual(ApprovalStatuses.Pending, approval.Status);
        Assert.AreEqual(
            SessionStatuses.PendingApproval,
            reloaded.Sessions.Sessions["session-1"].Status);

        await store.CloseAsync();
        await lease.ReleaseAsync();
    }

    [TestMethod]
    public async Task ClaimedApprovalCommitsResolutionAndSessionInOneStoreUpdate()
    {
        await WriteStoreAsync(SessionStatuses.Waiting, approvalStatus: null);
        await using var lease = new ActiveOwnerLeaseAcquirer(Options());
        await lease.AcquireAsync();
        var store = StoreOwner(lease);
        await store.OpenAsync();
        var owner = Owner(store, Origin);
        await owner.StartAsync(CancellationToken.None);
        await owner.HandleAsync(Event(
            "approval-requested",
            RuntimeEventTypes.ApprovalRequested,
            Origin.AddMinutes(2),
            new
            {
                requestId = "approval-new",
                title = "shell_command",
                description = "echo test",
                expiresAt = Origin.AddMinutes(22).ToString("O"),
            }));

        Assert.IsNull(await owner.TryClaimApprovalAsync("approval-new", "other-session"));
        Assert.IsNotNull(await owner.TryClaimApprovalAsync("approval-new", "session-1"));
        Assert.IsNull(await owner.TryClaimApprovalAsync("approval-new", "session-1"));
        var resolved = await owner.ResolveClaimedApprovalAsync(
            "approval-new",
            "session-1",
            ApprovalResolutions.Allow);

        Assert.IsNotNull(resolved);
        Assert.AreEqual(ApprovalStatuses.Resolved, resolved.Approval.Status);
        Assert.AreEqual(ApprovalResolutions.Allow, resolved.Approval.Resolution);
        Assert.AreEqual(SessionStatuses.Running, resolved.Session.Status);
        Assert.AreEqual(2, owner.Snapshot.Revision);
        Assert.IsFalse(owner.Snapshot.Approvals.Claims.Contains("approval-new"));
        var reloaded = await new BridgeJsonStoreRepository(directory!).LoadAsync();
        Assert.AreEqual(
            ApprovalStatuses.Resolved,
            reloaded.Approvals.Requests["approval-new"].Status);
        Assert.AreEqual(
            SessionStatuses.Running,
            reloaded.Sessions.Sessions["session-1"].Status);

        await store.CloseAsync();
        await lease.ReleaseAsync();
    }

    [TestMethod]
    public async Task ExpiredApprovalCommitsTimeoutAndReleasesAnyClaimDurably()
    {
        var store = new RecordingStoreOwner(SnapshotFromMemory());
        var owner = Owner(store, Origin.AddMinutes(30));
        await owner.StartAsync(CancellationToken.None);
        await owner.HandleAsync(Event(
            "session-started",
            RuntimeEventTypes.SessionStarted,
            Origin,
            new { }));
        await owner.HandleAsync(Event(
            "approval-requested",
            RuntimeEventTypes.ApprovalRequested,
            Origin.AddMinutes(2),
            new
            {
                requestId = "approval-expired",
                title = "shell_command",
                description = "echo test",
                expiresAt = Origin.AddMinutes(22).ToString("O"),
            }));
        Assert.IsNotNull(await owner.TryClaimApprovalAsync(
            "approval-expired",
            "session-1"));

        var expired = await owner.ExpireApprovalAsync("approval-expired");

        Assert.IsNotNull(expired);
        Assert.AreEqual(ApprovalStatuses.Resolved, expired.Status);
        Assert.AreEqual(ApprovalResolutions.Timeout, expired.Resolution);
        Assert.IsFalse(owner.Snapshot.Approvals.Claims.Contains("approval-expired"));
        Assert.AreEqual(
            SessionStatuses.Waiting,
            owner.Snapshot.Sessions.Sessions["session-1"].Status);
        Assert.AreEqual(
            ApprovalResolutions.Timeout,
            store.Current.Approvals.Requests["approval-expired"].Resolution);
        Assert.AreEqual(
            SessionStatuses.Waiting,
            store.Current.Sessions.Sessions["session-1"].Status);
        Assert.IsNull(await owner.ExpireApprovalAsync("approval-expired"));
    }

    [TestMethod]
    public async Task DeferredApprovalStaysPendingAndPersistsDesktopRequestFlag()
    {
        await WriteStoreAsync(SessionStatuses.Waiting, approvalStatus: null);
        await using var lease = new ActiveOwnerLeaseAcquirer(Options());
        await lease.AcquireAsync();
        var store = StoreOwner(lease);
        await store.OpenAsync();
        var owner = Owner(store, Origin);
        await owner.StartAsync(CancellationToken.None);
        await owner.HandleAsync(Event(
            "approval-requested",
            RuntimeEventTypes.ApprovalRequested,
            Origin.AddMinutes(2),
            new
            {
                requestId = "approval-new",
                title = "shell_command",
                description = "echo test",
                expiresAt = Origin.AddMinutes(22).ToString("O"),
            }));
        Assert.IsNotNull(await owner.TryClaimApprovalAsync(
            "approval-new",
            "session-1"));

        var deferred = await owner.DeferClaimedApprovalAsync(
            "approval-new",
            "session-1");

        Assert.IsNotNull(deferred);
        Assert.AreEqual(ApprovalStatuses.Pending, deferred.Approval.Status);
        Assert.AreEqual(
            SessionStatuses.PendingApproval,
            owner.Snapshot.Sessions.Sessions["session-1"].Status);
        Assert.IsFalse(owner.Snapshot.Approvals.Claims.Contains("approval-new"));
        var reloaded = await new BridgeJsonStoreRepository(directory!).LoadAsync();
        var approval = reloaded.Approvals.Requests["approval-new"];
        Assert.AreEqual(ApprovalStatuses.Pending, approval.Status);
        Assert.IsTrue(
            approval.ExtensionData!["desktopApprovalRequested"].GetBoolean());

        await store.CloseAsync();
        await lease.ReleaseAsync();
    }

    [TestMethod]
    public async Task FailedStoreCommitDoesNotPublishBusinessMutation()
    {
        await WriteStoreAsync(SessionStatuses.Waiting, approvalStatus: null);
        var initial = SnapshotFromDisk();
        var store = new RecordingStoreOwner(initial)
        {
            UpdateError = new IOException("simulated write failure"),
        };
        var owner = Owner(store, Origin);
        await owner.StartAsync(CancellationToken.None);

        await Assert.ThrowsExceptionAsync<IOException>(() => owner.HandleAsync(Event(
            "turn-started",
            RuntimeEventTypes.TurnStarted,
            Origin.AddMinutes(2),
            new { turnId = "turn-2" })));

        Assert.AreEqual(0, owner.Snapshot.Revision);
        Assert.AreEqual(
            SessionStatuses.Waiting,
            owner.Snapshot.Sessions.Sessions["session-1"].Status);
    }

    [TestMethod]
    public async Task InputStatePersistsInSessionsExtensionWithoutCreatingASeventhStoreFile()
    {
        await WriteStoreAsync(SessionStatuses.Waiting, approvalStatus: null);
        await using var lease = new ActiveOwnerLeaseAcquirer(Options());
        await lease.AcquireAsync();
        var store = StoreOwner(lease);
        await store.OpenAsync();
        var owner = Owner(store, Origin);
        await owner.StartAsync(CancellationToken.None);

        await owner.HandleAsync(Event(
            "input-requested",
            RuntimeEventTypes.InputRequested,
            Origin.AddMinutes(2),
            new
            {
                requestId = "input-1",
                expiresAt = Origin.AddMinutes(22).ToString("O"),
                questions = new[]
                {
                    new
                    {
                        id = "mode",
                        multiple = false,
                        allowsCustom = false,
                        options = new[] { "safe", "fast" },
                    },
                },
            }));

        Assert.AreEqual(1, owner.Snapshot.Inputs.Requests.Count);
        var reloaded = await new BridgeJsonStoreRepository(directory!).LoadAsync();
        var persisted = BridgeStoreCoreProjection.ProjectInputs(reloaded);
        Assert.AreEqual(1, persisted.Requests.Count);
        Assert.AreEqual("input-1", persisted.Requests["input-1"].RequestId);
        CollectionAssert.AreEquivalent(
            BridgeStoreFile.All.Select(file => file.FileName).ToArray(),
            Directory.EnumerateFiles(directory!, "*.json", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .ToArray());

        await store.CloseAsync();
        await lease.ReleaseAsync();
    }

    [TestMethod]
    public async Task PendingInputAndPartialAnswersSurviveStoreRoundTripAndOwnerRestart()
    {
        await WriteStoreAsync(SessionStatuses.Waiting, approvalStatus: null);
        var firstStore = new RecordingStoreOwner(SnapshotFromDisk());
        var firstOwner = Owner(firstStore, Origin.AddMinutes(3));
        await firstOwner.StartAsync(CancellationToken.None);
        await firstOwner.HandleAsync(Event(
            "input-requested",
            RuntimeEventTypes.InputRequested,
            Origin.AddMinutes(2),
            new
            {
                requestId = "input-1",
                expiresAt = Origin.AddMinutes(22).ToString("O"),
                questions = new object[]
                {
                    new
                    {
                        id = "mode",
                        header = "模式",
                        prompt = "请选择模式",
                        multiple = false,
                        allowsCustom = false,
                        options = new[] { "safe", "fast" },
                        isSecret = false,
                    },
                    new
                    {
                        id = "scope",
                        header = "范围",
                        prompt = "请选择范围",
                        multiple = true,
                        allowsCustom = false,
                        options = new[] { "code", "docs" },
                        isSecret = false,
                    },
                },
            }));
        var progress = await firstOwner.TryRecordInputAnswerAsync(
            "input-1",
            "session-1",
            "mode",
            ["safe"]);
        Assert.IsNotNull(progress);
        Assert.IsFalse(progress.Complete);

        var disk = new BridgeJsonStoreRepository(
            directory!,
            BridgeStoreAccess.ReadWriteCopy);
        await disk.WriteAsync(firstStore.Current);
        var roundTripped = await new BridgeJsonStoreRepository(directory!).LoadAsync();
        var restarted = Owner(
            new RecordingStoreOwner(roundTripped),
            Origin.AddMinutes(4));

        await restarted.StartAsync(CancellationToken.None);

        var restored = restarted.Snapshot.Inputs.Requests["input-1"];
        Assert.AreEqual(InputRequestStatuses.Pending, restored.Status);
        Assert.AreEqual("session-1", restored.SessionId);
        Assert.AreEqual("模式", restored.Questions[0].Header);
        Assert.AreEqual("请选择模式", restored.Questions[0].Prompt);
        CollectionAssert.AreEqual(new[] { "safe" }, restored.Answers["mode"].ToArray());
        Assert.IsFalse(restored.Answers.ContainsKey("scope"));
        CollectionAssert.AreEquivalent(
            BridgeStoreFile.All.Select(file => file.FileName).ToArray(),
            Directory.EnumerateFiles(directory!, "*.json", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .ToArray());
    }

    [TestMethod]
    public async Task InputAnswersClaimOnceAndCommitSessionOnlyAfterCompleteResolution()
    {
        await WriteStoreAsync(SessionStatuses.Waiting, approvalStatus: null);
        await using var lease = new ActiveOwnerLeaseAcquirer(Options());
        await lease.AcquireAsync();
        var store = StoreOwner(lease);
        await store.OpenAsync();
        var owner = Owner(store, Origin.AddMinutes(3));
        await owner.StartAsync(CancellationToken.None);
        await owner.HandleAsync(Event(
            "input-requested",
            RuntimeEventTypes.InputRequested,
            Origin.AddMinutes(2),
            new
            {
                requestId = "input-1",
                expiresAt = Origin.AddMinutes(22).ToString("O"),
                questions = new object[]
                {
                    new
                    {
                        id = "mode",
                        header = "模式",
                        prompt = "请选择模式",
                        multiple = false,
                        allowsCustom = false,
                        options = new[] { "safe", "fast" },
                        isSecret = false,
                    },
                    new
                    {
                        id = "scope",
                        header = "范围",
                        prompt = "请选择范围",
                        multiple = true,
                        allowsCustom = false,
                        options = new[] { "code", "docs" },
                        isSecret = false,
                    },
                },
            }));

        var first = await owner.TryRecordInputAnswerAsync(
            "input-1",
            "session-1",
            "mode",
            ["safe"]);
        var second = await owner.TryRecordInputAnswerAsync(
            "input-1",
            "session-1",
            "scope",
            ["code", "docs"]);
        var duplicate = await owner.TryClaimInputAsync("input-1", "session-1");
        var resolved = await owner.ResolveClaimedInputAsync("input-1", "session-1");

        Assert.IsNotNull(first);
        Assert.IsFalse(first.Complete);
        Assert.IsNotNull(second);
        Assert.IsTrue(second.Complete);
        Assert.IsNull(duplicate);
        Assert.IsNotNull(resolved);
        Assert.AreEqual(InputRequestStatuses.Resolved, resolved.Input.Status);
        Assert.AreEqual(SessionStatuses.Running, resolved.Session.Status);
        Assert.AreEqual("模式", resolved.Input.Questions[0].Header);
        Assert.AreEqual("请选择模式", resolved.Input.Questions[0].Prompt);
        var reloaded = await new BridgeJsonStoreRepository(directory!).LoadAsync();
        Assert.AreEqual(SessionStatuses.Running, reloaded.Sessions.Sessions["session-1"].Status);
        Assert.AreEqual(0, BridgeStoreCoreProjection.ProjectInputs(reloaded).Requests.Count);

        await store.CloseAsync();
        await lease.ReleaseAsync();
    }

    [TestMethod]
    public async Task ExternalInputCompletionUsesAnswersHeldByActiveClaim()
    {
        await WriteStoreAsync(SessionStatuses.Waiting, approvalStatus: null);
        await using var lease = new ActiveOwnerLeaseAcquirer(Options());
        await lease.AcquireAsync();
        var store = StoreOwner(lease);
        await store.OpenAsync();
        var owner = Owner(store, Origin.AddMinutes(3));
        await owner.StartAsync(CancellationToken.None);
        await owner.HandleAsync(Event(
            "input-requested",
            RuntimeEventTypes.InputRequested,
            Origin.AddMinutes(2),
            new
            {
                requestId = "input-1",
                expiresAt = Origin.AddMinutes(22).ToString("O"),
                questions = new[]
                {
                    new
                    {
                        id = "mode",
                        prompt = "请选择模式",
                        multiple = false,
                        allowsCustom = false,
                        options = new[] { "safe", "fast" },
                    },
                },
            }));
        var progress = await owner.TryRecordInputAnswerAsync(
            "input-1",
            "session-1",
            "mode",
            ["safe"]);

        await owner.HandleAsync(Event(
            "input-resolved",
            RuntimeEventTypes.InputResolvedExternally,
            Origin.AddMinutes(4),
            new { requestId = "input-1" }));
        var observed = await owner.ResolveClaimedInputAsync("input-1", "session-1");

        Assert.IsNotNull(progress);
        Assert.IsTrue(progress.Complete);
        Assert.IsNotNull(observed);
        Assert.AreEqual(InputRequestStatuses.Resolved, observed.Input.Status);
        CollectionAssert.AreEqual(new[] { "safe" }, observed.Input.Answers["mode"].ToArray());

        await store.CloseAsync();
        await lease.ReleaseAsync();
    }

    [TestMethod]
    public async Task DeferredManagedInputReturnsSessionToWaitingWithoutPersistingAnswers()
    {
        await WriteStoreAsync(SessionStatuses.Waiting, approvalStatus: null);
        await using var lease = new ActiveOwnerLeaseAcquirer(Options());
        await lease.AcquireAsync();
        var store = StoreOwner(lease);
        await store.OpenAsync();
        var owner = Owner(store, Origin.AddMinutes(3));
        await owner.StartAsync(CancellationToken.None);
        await owner.HandleAsync(Event(
            "input-requested",
            RuntimeEventTypes.InputRequested,
            Origin.AddMinutes(2),
            new
            {
                requestId = "input-1",
                expiresAt = Origin.AddMinutes(22).ToString("O"),
                questions = new[]
                {
                    new
                    {
                        id = "mode",
                        prompt = "请选择模式",
                        multiple = false,
                        allowsCustom = false,
                        options = new[] { "safe", "fast" },
                    },
                },
            }));
        Assert.IsNotNull(await owner.TryClaimInputAsync("input-1", "session-1"));

        var deferred = await owner.DeferClaimedInputAsync("input-1", "session-1");

        Assert.IsNotNull(deferred);
        Assert.AreEqual(InputRequestStatuses.Local, deferred.Input.Status);
        Assert.AreEqual(SessionStatuses.Waiting, deferred.Session.Status);
        Assert.AreEqual(0, deferred.Input.Answers.Count);
        var reloaded = await new BridgeJsonStoreRepository(directory!).LoadAsync();
        Assert.AreEqual(SessionStatuses.Waiting, reloaded.Sessions.Sessions["session-1"].Status);
        Assert.AreEqual(0, BridgeStoreCoreProjection.ProjectInputs(reloaded).Requests.Count);

        await store.CloseAsync();
        await lease.ReleaseAsync();
    }

    [TestMethod]
    public async Task SessionAliasUpdateIsAtomicAndRetainsUnknownExtensions()
    {
        var store = new RecordingStoreOwner(AliasSnapshot(
            AliasSession(
                "session-target",
                SessionStatuses.Waiting,
                extensions: new()
                {
                    ["futureSession"] = JsonSerializer.SerializeToElement("keep"),
                })));
        var owner = Owner(store, Origin);
        await owner.StartAsync(CancellationToken.None);

        var result = await owner.UpdateSessionAliasAsync(
            "session-target",
            "  主项目  ");

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(
            "主项目",
            result.Session!.ExtensionData!["alias"].GetString());
        Assert.AreEqual(
            "keep",
            result.Session.ExtensionData["futureSession"].GetString());
        Assert.AreEqual(
            "主项目",
            store.Current.Sessions.Sessions["session-target"]
                .ExtensionData!["alias"].GetString());
        Assert.AreEqual(0, owner.Snapshot.Revision);
    }

    [TestMethod]
    public async Task SessionAliasUpdateReservesVisibleHistoryAndIgnoresHiddenHistory()
    {
        var store = new RecordingStoreOwner(AliasSnapshot(
            AliasSession("session-target", SessionStatuses.Waiting),
            AliasSession(
                "session-visible-history",
                SessionStatuses.Ended,
                alias: "保留名",
                extensions: new()
                {
                    ["historyEligible"] = JsonSerializer.SerializeToElement(true),
                }),
            AliasSession(
                "session-hidden-history",
                SessionStatuses.Ended,
                alias: "隐藏名",
                extensions: new()
                {
                    ["historyEligible"] = JsonSerializer.SerializeToElement(true),
                    ["historyHiddenAt"] = JsonSerializer.SerializeToElement(
                        Origin.ToString("O")),
                }),
            AliasSession(
                "session-not-history",
                SessionStatuses.Ended,
                alias: "非历史名")));
        var owner = Owner(store, Origin);
        await owner.StartAsync(CancellationToken.None);

        var visibleConflict = await owner.UpdateSessionAliasAsync(
            "session-target",
            "保留名");
        Assert.AreEqual(0, store.Updates);
        var hiddenReuse = await owner.UpdateSessionAliasAsync(
            "session-target",
            "隐藏名");
        var nonHistoryReuse = await owner.UpdateSessionAliasAsync(
            "session-target",
            "非历史名");

        Assert.IsNotNull(visibleConflict.Conflict);
        Assert.IsFalse(visibleConflict.Succeeded);
        Assert.IsTrue(hiddenReuse.Succeeded);
        Assert.IsTrue(nonHistoryReuse.Succeeded);
        Assert.AreEqual(
            "非历史名",
            store.Current.Sessions.Sessions["session-target"]
                .ExtensionData!["alias"].GetString());
    }

    [TestMethod]
    public async Task VisibleHistoryAliasCanChangeWithoutLosingFeishuBinding()
    {
        var store = new RecordingStoreOwner(AliasSnapshot(
            AliasSession(
                "session-history",
                SessionStatuses.Ended,
                alias: "旧名称",
                extensions: new()
                {
                    ["historyEligible"] = JsonSerializer.SerializeToElement(true),
                    ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-history"),
                }),
            AliasSession(
                "session-hidden",
                SessionStatuses.Ended,
                extensions: new()
                {
                    ["historyEligible"] = JsonSerializer.SerializeToElement(true),
                    ["historyHiddenAt"] = JsonSerializer.SerializeToElement(
                        Origin.ToString("O")),
                })));
        var owner = Owner(store, Origin);
        await owner.StartAsync(CancellationToken.None);

        var renamed = await owner.UpdateSessionAliasAsync(
            "session-history",
            "归档会话");
        var cleared = await owner.UpdateSessionAliasAsync(
            "session-history",
            null);
        var hidden = await owner.UpdateSessionAliasAsync(
            "session-hidden",
            "不可见");

        Assert.IsTrue(renamed.Succeeded);
        Assert.IsTrue(cleared.Succeeded);
        Assert.IsFalse(hidden.Succeeded);
        Assert.AreEqual(
            "chat-history",
            store.Current.Sessions.Sessions["session-history"]
                .ExtensionData!["feishuChatId"].GetString());
        Assert.IsFalse(store.Current.Sessions.Sessions["session-history"]
            .ExtensionData!.ContainsKey("alias"));
    }

    [TestMethod]
    public async Task InvalidOrFailedSessionAliasUpdateDoesNotPublishMutation()
    {
        var store = new RecordingStoreOwner(AliasSnapshot(
            AliasSession("session-target", SessionStatuses.Waiting)));
        var owner = Owner(store, Origin);
        await owner.StartAsync(CancellationToken.None);

        var invalid = await owner.UpdateSessionAliasAsync(
            "session-target",
            "two words");
        Assert.IsFalse(invalid.Succeeded);
        Assert.IsNotNull(invalid.Error);
        Assert.IsFalse(store.Current.Sessions.Sessions["session-target"]
            .ExtensionData?.ContainsKey("alias") == true);

        store.UpdateError = new IOException("alias write failed");
        await Assert.ThrowsExceptionAsync<IOException>(() =>
            owner.UpdateSessionAliasAsync("session-target", "will-fail").AsTask());
        Assert.IsFalse(store.Current.Sessions.Sessions["session-target"]
            .ExtensionData?.ContainsKey("alias") == true);
        Assert.AreEqual(0, owner.Snapshot.Revision);
    }

    [TestMethod]
    public async Task ConcurrentSessionAliasUpdatesHaveOneWinner()
    {
        var store = new RecordingStoreOwner(AliasSnapshot(
            AliasSession("session-one", SessionStatuses.Waiting),
            AliasSession("session-two", SessionStatuses.Waiting)));
        var owner = Owner(store, Origin);
        await owner.StartAsync(CancellationToken.None);

        var results = await Task.WhenAll(
            owner.UpdateSessionAliasAsync("session-one", "同一名称").AsTask(),
            owner.UpdateSessionAliasAsync("session-two", "同一名称").AsTask());

        Assert.AreEqual(1, results.Count(result => result.Succeeded));
        Assert.AreEqual(1, store.Current.Sessions.Sessions.Values.Count(session =>
            session.ExtensionData?.TryGetValue("alias", out var value) == true &&
            value.GetString() == "同一名称"));
    }

    [TestMethod]
    public async Task HistoryHideIsIdempotentAndReleasesReservedAlias()
    {
        var store = new RecordingStoreOwner(AliasSnapshot(
            AliasSession("session-target", SessionStatuses.Waiting),
            AliasSession(
                "session-history",
                SessionStatuses.Ended,
                alias: "保留名",
                extensions: new()
                {
                    ["historyEligible"] = JsonSerializer.SerializeToElement(true),
                    ["futureSession"] = JsonSerializer.SerializeToElement("keep"),
                }),
            AliasSession(
                "managed-terminal-placeholder",
                SessionStatuses.Ended,
                extensions: new()
                {
                    ["historyEligible"] = JsonSerializer.SerializeToElement(true),
                })));
        var owner = Owner(store, Origin);
        await owner.StartAsync(CancellationToken.None);

        var conflict = await owner.UpdateSessionAliasAsync(
            "session-target",
            "保留名");
        var hidden = await owner.HideSessionFromHistoryAsync("session-history");
        var hiddenAgain = await owner.HideSessionFromHistoryAsync("session-history");
        var placeholder = await owner.HideSessionFromHistoryAsync(
            "managed-terminal-placeholder");
        var reused = await owner.UpdateSessionAliasAsync(
            "session-target",
            "保留名");

        Assert.IsNotNull(conflict.Conflict);
        Assert.IsTrue(hidden.Succeeded);
        Assert.IsTrue(hiddenAgain.Succeeded);
        Assert.AreEqual(
            Origin.ToString("O"),
            hidden.Session!.ExtensionData!["historyHiddenAt"].GetString());
        Assert.AreEqual(
            Origin.ToString("O"),
            hiddenAgain.Session!.ExtensionData!["historyHiddenAt"].GetString());
        Assert.AreEqual(
            "keep",
            hiddenAgain.Session.ExtensionData["futureSession"].GetString());
        Assert.IsFalse(placeholder.Succeeded);
        Assert.IsTrue(reused.Succeeded);
    }

    [TestMethod]
    public async Task SessionGroupNameUpdateIsAtomicAndRetainsUnknownExtensions()
    {
        var store = new RecordingStoreOwner(AliasSnapshot(
            AliasSession(
                "session-target",
                SessionStatuses.Waiting,
                alias: "新名称",
                extensions: new()
                {
                    ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-1"),
                    ["feishuChatName"] = JsonSerializer.SerializeToElement("old"),
                    ["futureSession"] = JsonSerializer.SerializeToElement("keep"),
                })));
        var owner = Owner(store, Origin);
        await owner.StartAsync(CancellationToken.None);

        var result = await owner.UpdateSessionGroupNameAsync(
            "session-target",
            "chat-1",
            "Codex｜新名称");

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(
            "Codex｜新名称",
            result.Session!.ExtensionData!["feishuChatName"].GetString());
        Assert.AreEqual(
            "keep",
            result.Session.ExtensionData["futureSession"].GetString());
        Assert.AreEqual(
            "新名称",
            result.Session.ExtensionData["alias"].GetString());
        Assert.AreEqual(
            "chat-1",
            store.Current.Sessions.Sessions["session-target"]
                .ExtensionData!["feishuChatId"].GetString());
    }

    [TestMethod]
    public async Task SessionGroupNameUpdateRejectsAReplacedBinding()
    {
        var store = new RecordingStoreOwner(AliasSnapshot(
            AliasSession(
                "session-target",
                SessionStatuses.Waiting,
                extensions: new()
                {
                    ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-new"),
                    ["feishuChatName"] = JsonSerializer.SerializeToElement("old"),
                })));
        var owner = Owner(store, Origin);
        await owner.StartAsync(CancellationToken.None);

        var result = await owner.UpdateSessionGroupNameAsync(
            "session-target",
            "chat-old",
            "Codex｜新名称");

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error!, "绑定已变化");
        Assert.AreEqual(0, store.Updates);
        Assert.AreEqual(
            "old",
            store.Current.Sessions.Sessions["session-target"]
                .ExtensionData!["feishuChatName"].GetString());
    }

    [TestMethod]
    public async Task SessionGroupNameUpdateDoesNotWriteWhenTheNameIsAlreadyCurrent()
    {
        var store = new RecordingStoreOwner(AliasSnapshot(
            AliasSession(
                "session-target",
                SessionStatuses.Waiting,
                extensions: new()
                {
                    ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-1"),
                    ["feishuChatName"] = JsonSerializer.SerializeToElement("Codex｜项目"),
                })));
        var owner = Owner(store, Origin);
        await owner.StartAsync(CancellationToken.None);

        var result = await owner.UpdateSessionGroupNameAsync(
            "session-target",
            "chat-1",
            "Codex｜项目");

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(0, store.Updates);
    }

    [TestMethod]
    public async Task FailedSessionGroupNameWriteDoesNotPublishMutation()
    {
        var store = new RecordingStoreOwner(AliasSnapshot(
            AliasSession(
                "session-target",
                SessionStatuses.Waiting,
                extensions: new()
                {
                    ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-1"),
                    ["feishuChatName"] = JsonSerializer.SerializeToElement("old"),
                })));
        var owner = Owner(store, Origin);
        await owner.StartAsync(CancellationToken.None);
        store.UpdateError = new IOException("group name write failed");

        await Assert.ThrowsExceptionAsync<IOException>(() =>
            owner.UpdateSessionGroupNameAsync(
                "session-target",
                "chat-1",
                "Codex｜新名称").AsTask());

        Assert.AreEqual(
            "old",
            store.Current.Sessions.Sessions["session-target"]
                .ExtensionData!["feishuChatName"].GetString());
        Assert.AreEqual(0, owner.Snapshot.Revision);
    }

    [TestMethod]
    public async Task SessionGroupClearRemovesBindingStateAndRetainsOrdinal()
    {
        var store = new RecordingStoreOwner(AliasSnapshot(
            AliasSession(
                "session-target",
                SessionStatuses.Ended,
                extensions: new()
                {
                    ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-old"),
                    ["feishuChatName"] = JsonSerializer.SerializeToElement("old"),
                    ["feishuChatCreatedAt"] =
                        JsonSerializer.SerializeToElement(Origin.AddDays(-8).ToString("O")),
                    ["feishuChatOrdinal"] = JsonSerializer.SerializeToElement(3),
                    ["feishuChatError"] = JsonSerializer.SerializeToElement("old error"),
                    ["feishuChatErrorAt"] =
                        JsonSerializer.SerializeToElement(Origin.ToString("O")),
                    ["futureSession"] = JsonSerializer.SerializeToElement("keep"),
                })));
        var owner = Owner(store, Origin);
        await owner.StartAsync(CancellationToken.None);

        var result = await owner.ClearSessionGroupAsync(
            "session-target",
            "chat-old");

        Assert.IsTrue(result.Succeeded);
        var extensions = result.Session!.ExtensionData!;
        Assert.IsFalse(extensions.ContainsKey("feishuChatId"));
        Assert.IsFalse(extensions.ContainsKey("feishuChatName"));
        Assert.IsFalse(extensions.ContainsKey("feishuChatCreatedAt"));
        Assert.IsFalse(extensions.ContainsKey("feishuChatError"));
        Assert.IsFalse(extensions.ContainsKey("feishuChatErrorAt"));
        Assert.AreEqual(3, extensions["feishuChatOrdinal"].GetInt32());
        Assert.AreEqual("keep", extensions["futureSession"].GetString());
    }

    [TestMethod]
    public async Task SessionGroupClearRejectsAReplacementBindingDuringWrite()
    {
        var store = new RecordingStoreOwner(AliasSnapshot(
            AliasSession(
                "session-target",
                SessionStatuses.Ended,
                extensions: new()
                {
                    ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-old"),
                    ["feishuChatName"] = JsonSerializer.SerializeToElement("old"),
                })))
        {
            BeforeUpdate = current => BridgeStoreBusinessStateMerger.PatchSessionExtensions(
                current,
                "session-target",
                new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                {
                    ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-new"),
                    ["feishuChatName"] = JsonSerializer.SerializeToElement("new"),
                }),
        };
        var owner = Owner(store, Origin);
        await owner.StartAsync(CancellationToken.None);

        var result = await owner.ClearSessionGroupAsync(
            "session-target",
            "chat-old");

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error!, "绑定已变化");
        Assert.AreEqual(
            "chat-new",
            store.Current.Sessions.Sessions["session-target"]
                .ExtensionData!["feishuChatId"].GetString());
        Assert.AreEqual(
            "new",
            store.Current.Sessions.Sessions["session-target"]
                .ExtensionData!["feishuChatName"].GetString());
    }

    [DataTestMethod]
    [DataRow("owner", "管理员绑定已变化")]
    [DataRow("ordinal", "序号已变化")]
    [DataRow("binding", "群绑定已变化")]
    public async Task SessionGroupErrorClearRejectsChangedBindingEvidence(
        string mutation,
        string expectedError)
    {
        var snapshot = AliasSnapshot(
            AliasSession(
                "session-target",
                SessionStatuses.Waiting,
                extensions: new()
                {
                    ["managedByAssistant"] = JsonSerializer.SerializeToElement(true),
                    ["feishuChatOrdinal"] = JsonSerializer.SerializeToElement(1),
                    ["feishuChatError"] =
                        JsonSerializer.SerializeToElement("old permission error"),
                    ["feishuChatErrorAt"] =
                        JsonSerializer.SerializeToElement(Origin.ToString("O")),
                })) with
        {
            Bindings = new BindingStoreDocument
            {
                OwnerOpenId = "owner",
            },
        };
        var store = new RecordingStoreOwner(snapshot)
        {
            BeforeUpdate = current => mutation switch
            {
                "owner" => current with
                {
                    Bindings = new BindingStoreDocument
                    {
                        OwnerOpenId = "owner-new",
                    },
                },
                "ordinal" => BridgeStoreBusinessStateMerger.PatchSessionExtensions(
                    current,
                    "session-target",
                    new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                    {
                        ["feishuChatOrdinal"] = JsonSerializer.SerializeToElement(2),
                    }),
                "binding" => BridgeStoreBusinessStateMerger.PatchSessionExtensions(
                    current,
                    "session-target",
                    new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                    {
                        ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-winner"),
                    }),
                _ => throw new AssertFailedException("未知测试变更。"),
            },
        };
        var owner = Owner(store, Origin);
        await owner.StartAsync(CancellationToken.None);

        var result = await owner.ClearSessionGroupErrorAsync(
            "session-target",
            expectedOrdinal: 1,
            expectedOwnerOpenId: "owner");

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error!, expectedError);
        Assert.AreEqual(
            "old permission error",
            store.Current.Sessions.Sessions["session-target"]
                .ExtensionData!["feishuChatError"].GetString());
        Assert.AreEqual(
            Origin.ToString("O"),
            store.Current.Sessions.Sessions["session-target"]
                .ExtensionData!["feishuChatErrorAt"].GetString());
    }

    [TestMethod]
    public async Task RejectsPassiveOptionsAndRequiresAnOpenProductionStore()
    {
        var closedStore = new RecordingStoreOwner(SnapshotFromMemory());
        var passive = new ActivePersistentBusinessStateOwner(
            BridgeHostOptions.Passive(directory!, port: 0),
            closedStore,
            new FixedTimeProvider(Origin));
        var passiveError = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            passive.StartAsync(CancellationToken.None));
        StringAssert.Contains(passiveError.Message, "只能用于 Active Host");

        closedStore.IsOpen = false;
        var active = Owner(closedStore, Origin);
        var storeError = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            active.StartAsync(CancellationToken.None));
        StringAssert.Contains(storeError.Message, "生产 Store");
        Assert.IsFalse(Directory.Exists(directory));
    }

    private ActivePersistentBusinessStateOwner Owner(
        IBridgeProductionStoreOwner store,
        DateTimeOffset now) => new(
            Options(),
            store,
            new FixedTimeProvider(now));

    private ActiveProductionStoreOwner StoreOwner(ActiveOwnerLeaseAcquirer lease) => new(
        Options(),
        lease,
        new BridgeJsonStoreRepository(directory!, BridgeStoreAccess.ReadWriteActiveOwner),
        new ActiveOwnerLeaseObserver(directory!).InspectAsync);

    private BridgeHostOptions Options() => new(
        directory!,
        IPAddress.Loopback,
        0,
        BridgeOwnershipMode.Active,
        "active-business-state-test");

    private RuntimeEventEnvelope Event(
        string eventId,
        string eventType,
        DateTimeOffset occurredAt,
        object payload,
        string runtime = RuntimeNames.Codex) => new()
        {
            ProtocolVersion = BridgeProtocolVersion.Current,
            Runtime = runtime,
            Session = new RuntimeSessionReference
            {
                ExternalId = "session-1",
                Cwd = "K:/repo",
            },
            TraceId = $"trace-{eventId}",
            CorrelationId = "turn-1",
            EventId = eventId,
            EventType = eventType,
            OccurredAt = occurredAt.ToString("O"),
            Payload = JsonSerializer.SerializeToElement(payload),
        };

    private async Task WriteStoreAsync(
        string sessionStatus,
        string? approvalStatus,
        bool includeExtensions = false)
    {
        Directory.CreateDirectory(directory!);
        var extension = includeExtensions
            ? ",\"futureSession\":\"keep-session\""
            : string.Empty;
        var rootExtension = includeExtensions
            ? ",\"futureRoot\":\"keep-root\""
            : string.Empty;
        var sessionsJson =
            "{\"sessions\":{\"session-1\":{\"sessionId\":\"session-1\"," +
            "\"shortId\":\"existing-short\",\"cwd\":\"K:/repo\"," +
            "\"projectName\":\"repo\",\"status\":\"" + sessionStatus +
            "\",\"runtime\":\"codex\",\"openedAt\":\"" + Origin.ToString("O") +
            "\",\"lastSeenAt\":\"" + Origin.AddMinutes(1).ToString("O") + "\"" +
            extension + "}}" + rootExtension + "}";
        await File.WriteAllTextAsync(
            Path.Combine(directory!, "sessions.json"),
            sessionsJson);
        if (approvalStatus is not null)
        {
            var approvalsJson =
                "{\"requests\":{\"approval-1\":{\"requestId\":\"approval-1\"," +
                "\"sessionId\":\"session-1\",\"turnId\":\"turn-1\"," +
                "\"cwd\":\"K:/repo\",\"toolName\":\"shell_command\"," +
                "\"toolPreview\":\"echo test\",\"createdAt\":\"" + Origin.ToString("O") +
                "\",\"expiresAt\":\"" + Origin.AddMinutes(20).ToString("O") +
                "\",\"status\":\"" + approvalStatus +
                "\",\"messageIds\":[],\"futureApproval\":\"keep-approval\"}}}";
            await File.WriteAllTextAsync(
                Path.Combine(directory!, "approvals.json"),
                approvalsJson);
        }
        await File.WriteAllTextAsync(
            Path.Combine(directory!, "settings.json"),
            "{\"future\":\"keep-settings\"}");
    }

    private BridgeStoreSnapshot SnapshotFromDisk() =>
        new BridgeJsonStoreRepository(directory!).LoadAsync().GetAwaiter().GetResult();

    private static BridgeStoreSnapshot SnapshotFromMemory() => new(
        new BindingStoreDocument(),
        new SessionStoreDocument(),
        new RouteStoreDocument(),
        new ApprovalStoreDocument(),
        new SettingsStoreDocument(),
        new ControlTokenStoreDocument());

    private static BridgeStoreSnapshot AliasSnapshot(
        params SessionStoreRecord[] sessions) => new(
        new BindingStoreDocument(),
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

    private static SessionStoreRecord AliasSession(
        string sessionId,
        string status,
        string? alias = null,
        Dictionary<string, JsonElement>? extensions = null) => new()
        {
            SessionId = sessionId,
            ShortId = sessionId[^Math.Min(8, sessionId.Length)..],
            Cwd = $"K:/workspace/{sessionId}",
            ProjectName = sessionId,
            Runtime = RuntimeNames.Codex,
            Status = status,
            OpenedAt = Origin.ToString("O"),
            LastSeenAt = Origin.AddMinutes(1).ToString("O"),
            EndedAt = status == SessionStatuses.Ended
                ? Origin.AddMinutes(2).ToString("O")
                : null,
            ExtensionData = AddAliasExtension(extensions, alias),
        };

    private static Dictionary<string, JsonElement>? AddAliasExtension(
        Dictionary<string, JsonElement>? extensions,
        string? alias)
    {
        var result = extensions is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(extensions, StringComparer.Ordinal);
        if (alias is not null)
        {
            result["alias"] = JsonSerializer.SerializeToElement(alias);
        }
        return result.Count == 0 ? null : result;
    }

    private sealed class RecordingStoreOwner(BridgeStoreSnapshot store)
        : IBridgeProductionStoreOwner
    {
        private BridgeStoreSnapshot current = store;

        public bool IsOpen { get; set; } = true;
        public Exception? UpdateError { get; set; }
        public Func<BridgeStoreSnapshot, BridgeStoreSnapshot>? BeforeUpdate { get; set; }
        public BridgeStoreSnapshot Current => current;
        public int Updates { get; private set; }
        public BridgeProductionStoreSnapshot Snapshot => new(
            IsOpen ? BridgeProductionStoreState.Open : BridgeProductionStoreState.NotOpened,
            null,
            0);

        public ValueTask OpenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<BridgeStoreSnapshot> ReadAsync(
            CancellationToken cancellationToken = default) =>
            IsOpen
                ? ValueTask.FromResult(current)
                : ValueTask.FromException<BridgeStoreSnapshot>(
                    new InvalidOperationException("生产 Store 尚未成功打开。"));

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask UpdateAsync(
            Func<BridgeStoreSnapshot, BridgeStoreSnapshot> update,
            CancellationToken cancellationToken = default)
        {
            if (UpdateError is not null)
            {
                return ValueTask.FromException(UpdateError);
            }
            var beforeUpdate = BeforeUpdate;
            BeforeUpdate = null;
            if (beforeUpdate is not null)
            {
                current = beforeUpdate(current);
            }
            Updates++;
            current = update(current);
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
