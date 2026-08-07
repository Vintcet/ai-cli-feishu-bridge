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
        var reloaded = await new NodeJsonStoreRepository(directory!).LoadAsync();
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
        var reloaded = await new NodeJsonStoreRepository(directory!).LoadAsync();
        Assert.AreEqual(
            SessionStatuses.Running,
            reloaded.Sessions.Sessions["session-1"].Status);
        Assert.AreEqual("existing-short", reloaded.Sessions.Sessions["session-1"].ShortId);
        Assert.AreEqual("keep-settings", reloaded.Settings.ExtensionData!["future"].GetString());

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
        var reloaded = await new NodeJsonStoreRepository(directory!).LoadAsync();
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
    public async Task ApprovalRequestPersistsRequiredNodeCompatibilityFields()
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

        var reloaded = await new NodeJsonStoreRepository(directory!).LoadAsync();
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
    public async Task InputStateRemainsRuntimeOnlyAndDoesNotCreateASeventhStoreFile()
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
        CollectionAssert.AreEquivalent(
            NodeStoreFile.All.Select(file => file.FileName).ToArray(),
            Directory.EnumerateFiles(directory!, "*.json", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .ToArray());

        await store.CloseAsync();
        await lease.ReleaseAsync();
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
        new NodeJsonStoreRepository(directory!, NodeStoreAccess.ReadWriteActiveOwner),
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
        object payload) => new()
        {
            ProtocolVersion = BridgeProtocolVersion.Current,
            Runtime = RuntimeNames.Codex,
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

    private NodeStoreSnapshot SnapshotFromDisk() =>
        new NodeJsonStoreRepository(directory!).LoadAsync().GetAwaiter().GetResult();

    private static NodeStoreSnapshot SnapshotFromMemory() => new(
        new BindingStoreDocument(),
        new SessionStoreDocument(),
        new RouteStoreDocument(),
        new ApprovalStoreDocument(),
        new SettingsStoreDocument(),
        new ControlTokenStoreDocument());

    private sealed class RecordingStoreOwner(NodeStoreSnapshot store)
        : IBridgeProductionStoreOwner
    {
        private NodeStoreSnapshot current = store;

        public bool IsOpen { get; set; } = true;
        public Exception? UpdateError { get; set; }
        public BridgeProductionStoreSnapshot Snapshot => new(
            IsOpen ? BridgeProductionStoreState.Open : BridgeProductionStoreState.NotOpened,
            null,
            0);

        public ValueTask OpenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<NodeStoreSnapshot> ReadAsync(
            CancellationToken cancellationToken = default) =>
            IsOpen
                ? ValueTask.FromResult(current)
                : ValueTask.FromException<NodeStoreSnapshot>(
                    new InvalidOperationException("生产 Store 尚未成功打开。"));

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask UpdateAsync(
            Func<NodeStoreSnapshot, NodeStoreSnapshot> update,
            CancellationToken cancellationToken = default)
        {
            if (UpdateError is not null)
            {
                return ValueTask.FromException(UpdateError);
            }
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
