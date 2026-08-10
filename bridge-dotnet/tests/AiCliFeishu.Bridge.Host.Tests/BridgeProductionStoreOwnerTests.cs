using System.Net;
using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class BridgeProductionStoreOwnerTests
{
    private string? directory;

    [TestInitialize]
    public void Initialize() => directory = Path.Combine(
        Path.GetTempPath(),
        $"bridge-active-store-{Guid.NewGuid():N}");

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task OpensExistingStoreAndBootstrapsMissingLocalConfiguration()
    {
        Directory.CreateDirectory(directory!);
        var sessions = Path.Combine(directory!, "sessions.json");
        const string source = """
            {"sessions":{"secret-session":{"sessionId":"secret-session","cwd":"K:\\secret","status":"waiting","runtime":"codex","lastSeenAt":"2026-08-06T00:00:00Z"}}}
            """;
        await File.WriteAllTextAsync(sessions, source);
        await using var lease = Lease();
        await lease.AcquireAsync();
        var owner = Owner(lease);

        await owner.OpenAsync();

        Assert.AreEqual(BridgeProductionStoreState.Open, owner.Snapshot.State);
        Assert.AreEqual(6, owner.Snapshot.StoreFiles);
        Assert.IsNull(owner.Snapshot.Store);
        Assert.AreEqual(1, owner.CurrentSnapshot.Store!.Sessions.Sessions.Count);
        Assert.AreEqual(
            new BridgeComponentHealth("production-store", "ready", "loaded files=6"),
            owner.ComponentHealth);
        var controlStatus = ((IBridgeControlStoreStatusSource)owner).Status;
        Assert.AreEqual(BridgeStoreViewStatuses.Loaded, controlStatus.Status);
        Assert.AreEqual(6, controlStatus.Files);
        Assert.AreEqual(1, controlStatus.Sessions);
        Assert.AreEqual(1, controlStatus.ActiveSessions);
        Assert.AreEqual(0, controlStatus.EndedSessions);
        Assert.AreEqual(0, controlStatus.Bindings);
        Assert.AreEqual(0, controlStatus.Routes);
        Assert.AreEqual(0, controlStatus.Approvals);
        await ((IBridgeControlStoreStatusSource)owner).RefreshAsync();
        Assert.AreEqual(BridgeProductionStoreState.Open, owner.Snapshot.State);
        Assert.AreEqual(10, owner.CurrentSnapshot.Store.Bindings.PairingCode!.Length);
        Assert.IsTrue(owner.CurrentSnapshot.Store.Bindings.PairingCode.All(Uri.IsHexDigit));
        Assert.AreEqual(64, owner.CurrentSnapshot.Store.ControlToken.Token!.Length);
        Assert.IsTrue(owner.CurrentSnapshot.Store.ControlToken.Token.All(Uri.IsHexDigit));
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            owner.CurrentSnapshot.Store.Settings.WorkspaceRoot));
        foreach (var file in BridgeStoreFile.All)
        {
            Assert.IsTrue(File.Exists(Path.Combine(directory!, file.FileName)));
        }
        Assert.IsTrue(File.Exists(Path.Combine(directory!, ".bridge-store.commit")));
        Assert.IsTrue(File.Exists(lease.MetadataPath));
    }

    [TestMethod]
    public async Task PreservesValidBootstrapValuesAndClearsObsoletePairingCode()
    {
        Directory.CreateDirectory(directory!);
        const string token =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        await File.WriteAllTextAsync(
            Path.Combine(directory!, "bindings.json"),
            "{\"users\":{},\"ownerOpenId\":\"owner\",\"pairingCode\":\"OLD-CODE\"}");
        await File.WriteAllTextAsync(
            Path.Combine(directory!, "settings.json"),
            "{\"workspaceRoot\":\"K:\\\\workspace\"}");
        await File.WriteAllTextAsync(
            Path.Combine(directory!, "control-token.json"),
            $"{{\"token\":\"{token}\"}}");
        await using var lease = Lease();
        await lease.AcquireAsync();
        var owner = Owner(lease);

        await owner.OpenAsync();

        Assert.IsNull(owner.CurrentSnapshot.Store!.Bindings.PairingCode);
        Assert.AreEqual(token, owner.CurrentSnapshot.Store.ControlToken.Token);
        Assert.AreEqual("K:\\workspace", owner.CurrentSnapshot.Store.Settings.WorkspaceRoot);
    }

    [TestMethod]
    public async Task RejectsOpeningBeforeLeaseWithoutAccessingStore()
    {
        var lease = Lease();
        var owner = Owner(lease);

        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await owner.OpenAsync());

        StringAssert.Contains(error.Message, "租约持有期间");
        Assert.AreEqual(BridgeProductionStoreState.NotOpened, owner.Snapshot.State);
        Assert.IsFalse(Directory.Exists(directory));
    }

    [TestMethod]
    public async Task RejectsReplacedLeaseBeforeWritingProductionStore()
    {
        Directory.CreateDirectory(directory!);
        var settings = Path.Combine(directory!, "settings.json");
        const string source = "{\"notifyActivity\":false}";
        await File.WriteAllTextAsync(settings, source);
        await using var lease = Lease();
        await lease.AcquireAsync();
        var observed = new ActiveOwnerLeaseSnapshot(
            ActiveOwnerLeaseState.Live,
            lease.Record with { LeaseId = "replacement-owner" });
        var owner = Owner(lease, _ => ValueTask.FromResult(observed));

        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await owner.OpenAsync());

        StringAssert.Contains(error.Message, "身份已变化");
        Assert.AreEqual(source, await File.ReadAllTextAsync(settings));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "settings.json",
                ActiveOwnerLeaseObserver.MetadataFileName,
                ActiveOwnerLeaseObserver.OwnershipHandleFileName,
            },
            Directory.EnumerateFiles(directory!, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .ToArray());
    }

    [TestMethod]
    public async Task CloseIgnoresCancelledShutdownAndFlushesBeforeLeaseRelease()
    {
        Directory.CreateDirectory(directory!);
        var sessions = Path.Combine(directory!, "sessions.json");
        await File.WriteAllTextAsync(sessions, "{\"sessions\":{}}");
        await using var lease = Lease();
        await lease.AcquireAsync();
        var owner = Owner(lease);
        await owner.OpenAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await owner.CloseAsync(cancelled.Token);

        Assert.AreEqual(BridgeProductionStoreState.Closed, owner.Snapshot.State);
        Assert.IsNull(owner.Snapshot.Store);
        Assert.IsTrue(lease.IsHeld);
        Assert.IsTrue(Directory.Exists(lease.LockDirectoryPath));
        CollectionAssert.AreEquivalent(
            BridgeStoreFile.All.Select(file => file.FileName).ToArray(),
            Directory.EnumerateFiles(directory!, "*.json", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .ToArray());
        Assert.IsFalse(Directory.EnumerateFiles(directory!, "*.tmp").Any());

        await lease.ReleaseAsync();
        Assert.IsFalse(Directory.Exists(lease.LockDirectoryPath));
    }

    [TestMethod]
    public async Task UpdateFinishesDurableCommitWhenRequestCancelsAfterLeaseCheck()
    {
        Directory.CreateDirectory(directory!);
        await File.WriteAllTextAsync(
            Path.Combine(directory!, "sessions.json"),
            "{\"sessions\":{}}");
        await using var lease = Lease();
        await lease.AcquireAsync();
        using var request = new CancellationTokenSource();
        var inspections = 0;
        var owner = Owner(
            lease,
            cancellationToken =>
            {
                inspections++;
                if (inspections == 3)
                {
                    request.Cancel();
                    return ValueTask.FromResult(new ActiveOwnerLeaseSnapshot(
                        ActiveOwnerLeaseState.Live,
                        lease.Record));
                }
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(new ActiveOwnerLeaseSnapshot(
                    ActiveOwnerLeaseState.Live,
                    lease.Record));
            });
        await owner.OpenAsync();

        await owner.UpdateAsync(
            store => store with
            {
                Settings = new SettingsStoreDocument { NotifyActivity = true },
            },
            request.Token);

        Assert.IsTrue(request.IsCancellationRequested);
        Assert.AreEqual(BridgeProductionStoreState.Open, owner.Snapshot.State);
        Assert.IsTrue(owner.CurrentSnapshot.Store!.Settings.NotifyActivity);
        var persisted = await File.ReadAllTextAsync(
            Path.Combine(directory!, "settings.json"));
        StringAssert.Contains(persisted, "\"notifyActivity\":true");
    }

    [TestMethod]
    public async Task UpdateRetriesATransientLeaseObservation()
    {
        Directory.CreateDirectory(directory!);
        await File.WriteAllTextAsync(
            Path.Combine(directory!, "sessions.json"),
            "{\"sessions\":{}}");
        await using var lease = Lease();
        await lease.AcquireAsync();
        var inspections = 0;
        var owner = Owner(
            lease,
            _ =>
            {
                inspections++;
                return ValueTask.FromResult(inspections == 4
                    ? new ActiveOwnerLeaseSnapshot(ActiveOwnerLeaseState.Invalid)
                    : new ActiveOwnerLeaseSnapshot(
                        ActiveOwnerLeaseState.Live,
                        lease.Record));
            });
        await owner.OpenAsync();

        await owner.UpdateAsync(store => store with
        {
            Settings = new SettingsStoreDocument { NotifyActivity = true },
        });

        Assert.AreEqual(5, inspections);
        Assert.AreEqual(BridgeProductionStoreState.Open, owner.Snapshot.State);
        Assert.IsTrue(owner.CurrentSnapshot.Store!.Settings.NotifyActivity);
    }

    [TestMethod]
    public async Task FailedUpdateRequiresReloadBeforeAnyFurtherMutation()
    {
        Directory.CreateDirectory(directory!);
        await File.WriteAllTextAsync(
            Path.Combine(directory!, "sessions.json"),
            "{\"sessions\":{}}");
        await using var lease = Lease();
        await lease.AcquireAsync();
        var inspections = 0;
        var owner = Owner(
            lease,
            _ =>
            {
                inspections++;
                return ValueTask.FromResult(new ActiveOwnerLeaseSnapshot(
                    ActiveOwnerLeaseState.Live,
                    inspections == 4
                        ? lease.Record with { LeaseId = "replacement-owner" }
                        : lease.Record));
            });
        await owner.OpenAsync();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await owner.UpdateAsync(store => store with
            {
                Settings = new SettingsStoreDocument { NotifyActivity = true },
            }));

        Assert.AreEqual(BridgeProductionStoreState.Failed, owner.Snapshot.State);
        StringAssert.Contains(owner.ComponentHealth.Detail!, "store-update-failed");
        Assert.IsNotNull(owner.CurrentSnapshot.Store);
        var projection = await owner.ReadForProjectionAsync();
        Assert.AreNotEqual(true, projection.Settings.NotifyActivity);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await owner.UpdateAsync(store => store with
            {
                Settings = new SettingsStoreDocument { AutoApprove = true },
            }));

        await owner.OpenAsync();
        Assert.IsTrue(owner.CurrentSnapshot.Store!.Settings.NotifyActivity);
        await owner.UpdateAsync(store => store with
        {
            Settings = new SettingsStoreDocument
            {
                NotifyActivity = true,
                AutoApprove = true,
            },
        });

        Assert.AreEqual(BridgeProductionStoreState.Open, owner.Snapshot.State);
        Assert.IsTrue(owner.CurrentSnapshot.Store!.Settings.AutoApprove);
    }

    [TestMethod]
    public async Task CorruptWritableStoreFailsClosedAndReportsFailedHealth()
    {
        Directory.CreateDirectory(directory!);
        await File.WriteAllTextAsync(
            Path.Combine(directory!, "sessions.json"),
            "{\"sessions\":{\"broken\":{}}}");
        await using var lease = Lease();
        await lease.AcquireAsync();
        var owner = Owner(lease);

        var error = await Assert.ThrowsExceptionAsync<BridgeStoreCorruptionException>(async () =>
            await owner.OpenAsync());

        Assert.AreEqual("sessions.json", error.LogicalFile);
        Assert.AreEqual(BridgeProductionStoreState.Failed, owner.Snapshot.State);
        Assert.AreEqual("failed", owner.ComponentHealth.Status);
        StringAssert.Contains(owner.ComponentHealth.Detail!, "store-open-failed");
        Assert.AreEqual(
            1,
            Directory.EnumerateFiles(directory!, "sessions.json.corrupt-*").Count());
    }

    [TestMethod]
    public async Task AuditFailureLeavesStoreCommittedButMakesHealthFail()
    {
        Directory.CreateDirectory(directory!);
        await File.WriteAllTextAsync(
            Path.Combine(directory!, "sessions.json"),
            """
            {"sessions":{"session-1":{"sessionId":"session-1","cwd":"K:\\project","status":"waiting","runtime":"codex","openedAt":"2026-08-06T00:00:00Z","lastSeenAt":"2026-08-06T00:00:00Z"}}}
            """);
        await using var lease = Lease();
        await lease.AcquireAsync();
        var owner = Owner(lease, audit: new FailingAuditLog());
        await owner.OpenAsync();

        await owner.UpdateAsync(store => store with
        {
            Approvals = new ApprovalStoreDocument
            {
                Requests = new Dictionary<string, ApprovalStoreRecord>(StringComparer.Ordinal)
                {
                    ["approval-1"] = new()
                    {
                        RequestId = "approval-1",
                        SessionId = "session-1",
                        TurnId = "turn-1",
                        Cwd = "K:\\project",
                        ToolName = "shell_command",
                        ToolPreview = "preview",
                        CreatedAt = "2026-08-06T00:00:00Z",
                        ExpiresAt = "2026-08-06T00:10:00Z",
                        Status = "pending",
                    },
                },
            },
        });

        Assert.AreEqual(BridgeProductionStoreState.Open, owner.Snapshot.State);
        Assert.AreEqual("failed", owner.ComponentHealth.Status);
        StringAssert.Contains(owner.ComponentHealth.Detail!, "approval-audit-failed");
        var persisted = await new BridgeJsonStoreRepository(directory!).LoadAsync();
        Assert.IsTrue(persisted.Approvals.Requests.ContainsKey("approval-1"));
    }

    [TestMethod]
    public void RejectsPassiveOptionsAndCopyRepositoryBeforeFileAccess()
    {
        var passive = BridgeHostOptions.Passive(directory!, port: 0);
        var lease = new RecordingLease();

        var passiveError = Assert.ThrowsException<InvalidOperationException>(() =>
            _ = new ActiveProductionStoreOwner(
                passive,
                lease,
                new BridgeJsonStoreRepository(directory!, BridgeStoreAccess.ReadWriteActiveOwner)));
        StringAssert.Contains(passiveError.Message, "只能用于 Active Host");

        var copyError = Assert.ThrowsException<InvalidOperationException>(() =>
            _ = new ActiveProductionStoreOwner(
                Options(),
                lease,
                new BridgeJsonStoreRepository(directory!, BridgeStoreAccess.ReadWriteCopy)));
        StringAssert.Contains(copyError.Message, "Active Owner 专用写入");
        Assert.IsFalse(Directory.Exists(directory));
    }

    private BridgeHostOptions Options() => new(
        directory!,
        IPAddress.Loopback,
        0,
        BridgeOwnershipMode.Active,
        "active-store-test");

    private ActiveOwnerLeaseAcquirer Lease() => new(Options());

    private ActiveProductionStoreOwner Owner(
        ActiveOwnerLeaseAcquirer lease,
        Func<CancellationToken, ValueTask<ActiveOwnerLeaseSnapshot>>? inspect = null,
        IApprovalAuditLog? audit = null) => new(
            Options(),
            lease,
            new BridgeJsonStoreRepository(
                directory!,
                BridgeStoreAccess.ReadWriteActiveOwner),
            inspect,
            audit);

    private sealed class FailingAuditLog : IApprovalAuditLog
    {
        public Task AppendChangesAsync(
            ApprovalStoreDocument before,
            ApprovalStoreDocument after,
            CancellationToken cancellationToken = default) =>
            throw new IOException("audit unavailable");
    }

    private sealed class RecordingLease : IBridgeActiveOwnerLeaseLifecycle
    {
        public bool IsHeld => false;

        public ActiveOwnerLeaseRecord? HeldLease => null;

        public ValueTask<ActiveOwnerLeaseRecord> AcquireAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask ReleaseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
