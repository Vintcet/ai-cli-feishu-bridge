using System.Net;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActiveManagedTerminalDirectoryTests
{
    private static readonly DateTimeOffset Origin =
        DateTimeOffset.Parse("2026-08-07T08:00:00.000Z");

    [TestMethod]
    public async Task RestoresPersistedBindingAfterMatchingHeartbeat()
    {
        var cwd = ProjectPath("restore");
        var store = new RecordingStoreOwner(Store(
            Session(
                "session-restore",
                cwd,
                RuntimeNames.Codex,
                "waiting",
                "startup",
                "terminal-restore")));
        var directory = Directory(store);

        await directory.StartAsync(CancellationToken.None);

        Assert.AreEqual(
            new BridgeManagedTerminalDirectorySnapshot(true, 0, 0, 0, 0),
            directory.Snapshot);
        directory.Register(new(
            "terminal-restore",
            cwd,
            RuntimeNames.Codex,
            Elevated: false,
            Ready: true));
        var target = directory.FindBySession("session-restore");
        Assert.IsNotNull(target);
        Assert.AreEqual("terminal-restore", target.TerminalId);
        Assert.AreEqual("session-restore", target.SessionExternalId);
        Assert.IsTrue(target.Ready);
        Assert.IsTrue(target.Generation > 0);
        Assert.IsTrue(directory.IsCurrent(target));
        Assert.AreEqual(1, store.Reads);
    }

    [TestMethod]
    public async Task IgnoresEndedAndManagedWindowPlaceholderSessions()
    {
        var cwd = ProjectPath("ignored");
        var store = new RecordingStoreOwner(Store(
            Session(
                "session-ended",
                ProjectPath("old-ended"),
                RuntimeNames.ClaudeCode,
                "ended",
                "startup",
                "terminal-ended"),
            Session(
                "session-placeholder",
                ProjectPath("old-placeholder"),
                RuntimeNames.ClaudeCode,
                "ready",
                "managed_window",
                "terminal-placeholder"),
            SessionWithExtension(
                "session-unbound-future-source",
                cwd,
                RuntimeNames.Codex,
                "waiting",
                ("source", 42))));
        var directory = Directory(store);
        await directory.StartAsync(CancellationToken.None);

        directory.Register(new(
            "terminal-ended",
            cwd,
            RuntimeNames.Codex,
            Elevated: false,
            Ready: true));
        directory.Register(new(
            "terminal-placeholder",
            cwd,
            RuntimeNames.Codex,
            Elevated: false,
            Ready: true));

        Assert.IsNotNull(directory.ClaimById(
            "terminal-ended",
            cwd,
            RuntimeNames.Codex,
            "session-new-ended"));
        Assert.IsNotNull(directory.ClaimById(
            "terminal-placeholder",
            cwd,
            RuntimeNames.Codex,
            "session-new-placeholder"));
    }

    [TestMethod]
    public async Task RejectsConflictingAndMalformedPersistedBindings()
    {
        var cwd = ProjectPath("conflict");
        var snapshots = new[]
        {
            Store(
                Session("session-one", cwd, RuntimeNames.Codex, "waiting", "startup", "terminal-shared"),
                Session("session-two", cwd, RuntimeNames.Codex, "waiting", "startup", "terminal-shared")),
            Store(
                Session("session-shared", cwd, RuntimeNames.Codex, "waiting", "startup", "terminal-one"),
                Session("session-shared", cwd, RuntimeNames.Codex, "waiting", "startup", "terminal-two")),
            Store(Session(
                "session-invalid-runtime",
                cwd,
                "invalid-runtime",
                "waiting",
                "startup",
                "terminal-runtime")),
            Store(Session(
                "session-invalid-cwd",
                "relative-path",
                RuntimeNames.Codex,
                "waiting",
                "startup",
                "terminal-cwd")),
            Store(SessionWithExtension(
                "session-invalid-extension",
                cwd,
                RuntimeNames.Codex,
                "waiting",
                ("source", "startup"),
                ("managedTerminalId", 42))),
        };

        foreach (var snapshot in snapshots)
        {
            var directory = Directory(new RecordingStoreOwner(snapshot));
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
                directory.StartAsync(CancellationToken.None));
            Assert.IsFalse(directory.Snapshot.Initialized);
        }
    }

    [TestMethod]
    public async Task HeartbeatsFixIdentityAndReplacementChangesGeneration()
    {
        var cwd = ProjectPath("identity");
        var clock = new MutableTimeProvider(Origin);
        var directory = Directory(new RecordingStoreOwner(Store()), clock);
        await directory.StartAsync(CancellationToken.None);
        var heartbeat = new BridgeManagedTerminalRegistration(
            "terminal-identity",
            cwd,
            RuntimeNames.Codex,
            Elevated: true,
            Ready: true);
        directory.Register(heartbeat);
        directory.ClaimById(
            heartbeat.TerminalId,
            cwd,
            RuntimeNames.Codex,
            "session-identity");
        var original = directory.FindBySession("session-identity")!;

        clock.Advance(TimeSpan.FromSeconds(1));
        directory.Register(heartbeat with { Ready = false });
        Assert.IsFalse(directory.FindBySession("session-identity")!.Ready);
        Assert.IsFalse(directory.IsCurrent(original));
        Assert.ThrowsException<InvalidOperationException>(() =>
            directory.Register(heartbeat with { Cwd = ProjectPath("other") }));
        Assert.ThrowsException<InvalidOperationException>(() =>
            directory.Register(heartbeat with { Runtime = RuntimeNames.ClaudeCode }));
        Assert.ThrowsException<InvalidOperationException>(() =>
            directory.Register(heartbeat with { Elevated = false }));

        Assert.IsTrue(directory.Unregister(heartbeat.TerminalId));
        clock.Advance(TimeSpan.FromSeconds(1));
        directory.Register(heartbeat);
        directory.ClaimById(
            heartbeat.TerminalId,
            cwd,
            RuntimeNames.Codex,
            "session-identity");
        var replacement = directory.FindBySession("session-identity")!;
        Assert.IsTrue(replacement.Generation > original.Generation);
        Assert.IsFalse(directory.IsCurrent(original));
        Assert.IsTrue(directory.IsCurrent(replacement));
    }

    [TestMethod]
    public async Task AppliesOnlineAndRetentionWindowsWithoutReusingGeneration()
    {
        var cwd = ProjectPath("lifetime");
        var clock = new MutableTimeProvider(Origin);
        var directory = Directory(new RecordingStoreOwner(Store()), clock);
        await directory.StartAsync(CancellationToken.None);
        directory.Register(new(
            "terminal-lifetime",
            cwd,
            RuntimeNames.Codex,
            Elevated: false,
            Ready: true));
        directory.ClaimById(
            "terminal-lifetime",
            cwd,
            RuntimeNames.Codex,
            "session-lifetime");
        var original = directory.FindBySession("session-lifetime")!;

        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.IsNotNull(directory.FindBySession("session-lifetime"));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.IsNull(directory.FindBySession("session-lifetime"));
        Assert.AreEqual(
            new BridgeManagedTerminalDirectorySnapshot(true, 1, 0, 0, 1),
            directory.Snapshot);
        clock.Set(Origin.AddSeconds(60));
        Assert.AreEqual(1, directory.Snapshot.Registrations);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(0, directory.Snapshot.Registrations);

        directory.Register(new(
            "terminal-lifetime",
            cwd,
            RuntimeNames.Codex,
            Elevated: false,
            Ready: true));
        directory.ClaimById(
            "terminal-lifetime",
            cwd,
            RuntimeNames.Codex,
            "session-lifetime");
        var replacement = directory.FindBySession("session-lifetime")!;
        Assert.IsTrue(replacement.Generation > original.Generation);
        Assert.IsFalse(directory.IsCurrent(original));
    }

    [TestMethod]
    public async Task ClaimsOldestAvailableTerminalByCwdAndRuntime()
    {
        var cwd = ProjectPath("claim-order");
        var clock = new MutableTimeProvider(Origin);
        var directory = Directory(new RecordingStoreOwner(Store()), clock);
        await directory.StartAsync(CancellationToken.None);
        directory.Register(Registration("terminal-first", cwd, RuntimeNames.Codex));
        clock.Advance(TimeSpan.FromSeconds(1));
        directory.Register(Registration("terminal-second", cwd, RuntimeNames.Codex));
        clock.Advance(TimeSpan.FromSeconds(1));
        directory.Register(Registration("terminal-claude", cwd, RuntimeNames.ClaudeCode));

        Assert.AreEqual(
            "terminal-first",
            directory.Claim(cwd, RuntimeNames.Codex, "session-first")?.TerminalId);
        Assert.AreEqual(
            "terminal-second",
            directory.Claim(cwd, RuntimeNames.Codex, "session-second")?.TerminalId);
        Assert.IsNull(directory.Claim(cwd, RuntimeNames.Codex, "session-third"));
        Assert.AreEqual(
            "terminal-claude",
            directory.Claim(cwd, RuntimeNames.ClaudeCode, "session-claude")?.TerminalId);
    }

    [TestMethod]
    public async Task ClaimByIdRejectsIdentityAndSessionCrossWiring()
    {
        var cwd = ProjectPath("claim-by-id");
        var directory = Directory(new RecordingStoreOwner(Store()));
        await directory.StartAsync(CancellationToken.None);
        directory.Register(Registration("terminal-owner", cwd, RuntimeNames.Codex));
        directory.Register(Registration("terminal-other", cwd, RuntimeNames.Codex));
        directory.ClaimById(
            "terminal-owner",
            cwd,
            RuntimeNames.Codex,
            "session-owner");

        Assert.ThrowsException<InvalidOperationException>(() =>
            directory.ClaimById(
                "terminal-owner",
                ProjectPath("wrong-cwd"),
                RuntimeNames.Codex,
                "session-owner"));
        Assert.ThrowsException<InvalidOperationException>(() =>
            directory.ClaimById(
                "terminal-owner",
                cwd,
                RuntimeNames.ClaudeCode,
                "session-owner"));
        Assert.ThrowsException<InvalidOperationException>(() =>
            directory.ClaimById(
                "terminal-owner",
                cwd,
                RuntimeNames.Codex,
                "session-other"));
        Assert.ThrowsException<InvalidOperationException>(() =>
            directory.ClaimById(
                "terminal-other",
                cwd,
                RuntimeNames.Codex,
                "session-owner"));
        Assert.AreEqual(
            "terminal-owner",
            directory.FindBySession("session-owner")?.TerminalId);
    }

    [TestMethod]
    public async Task ReleaseAndUnregisterRemoveInMemoryOwnership()
    {
        var cwd = ProjectPath("release");
        var directory = Directory(new RecordingStoreOwner(Store()));
        await directory.StartAsync(CancellationToken.None);
        directory.Register(Registration("terminal-release", cwd, RuntimeNames.Codex));
        directory.ClaimById(
            "terminal-release",
            cwd,
            RuntimeNames.Codex,
            "session-before-release");

        directory.Release("session-before-release");
        Assert.IsNull(directory.FindBySession("session-before-release"));
        Assert.AreEqual(
            "terminal-release",
            directory.Claim(cwd, RuntimeNames.Codex, "session-after-release")?.TerminalId);
        Assert.IsTrue(directory.Unregister("terminal-release"));
        Assert.IsNull(directory.FindBySession("session-after-release"));
        Assert.IsFalse(directory.Unregister("terminal-release"));
    }

    [TestMethod]
    public async Task SupportsConcurrentRegistrationClaimAndLookup()
    {
        var cwd = ProjectPath("concurrent");
        var directory = Directory(new RecordingStoreOwner(Store()));
        await directory.StartAsync(CancellationToken.None);
        var terminalIds = Enumerable.Range(0, 64)
            .Select(index => $"terminal-{index:D3}")
            .ToArray();

        Parallel.ForEach(terminalIds, terminalId =>
            directory.Register(Registration(terminalId, cwd, RuntimeNames.Codex)));
        Parallel.ForEach(terminalIds, terminalId =>
            directory.ClaimById(
                terminalId,
                cwd,
                RuntimeNames.Codex,
                $"session-{terminalId}"));
        Parallel.ForEach(terminalIds, terminalId =>
        {
            var target = directory.FindBySession($"session-{terminalId}");
            Assert.AreEqual(terminalId, target?.TerminalId);
        });

        Assert.AreEqual(
            new BridgeManagedTerminalDirectorySnapshot(true, 64, 64, 64, 64),
            directory.Snapshot);
    }

    [TestMethod]
    public async Task RejectsPassiveOptionsAndRequiresOpenStore()
    {
        var passiveStore = new RecordingStoreOwner(Store());
        var passive = new ActiveManagedTerminalDirectory(
            BridgeHostOptions.Passive(ProjectPath("passive"), port: 0),
            passiveStore,
            new MutableTimeProvider(Origin));

        var passiveError = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            passive.StartAsync(CancellationToken.None));
        StringAssert.Contains(passiveError.Message, "只能用于 Active Host");
        Assert.AreEqual(0, passiveStore.Reads);

        var closedStore = new RecordingStoreOwner(Store()) { IsOpen = false };
        var active = Directory(closedStore);
        var storeError = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            active.StartAsync(CancellationToken.None));
        StringAssert.Contains(storeError.Message, "生产 Store");
        Assert.IsFalse(active.Snapshot.Initialized);
    }

    [TestMethod]
    public async Task LifecycleIsIdempotentAndHealthDoesNotExposeIdentities()
    {
        var cwd = ProjectPath("sensitive-project-path");
        var store = new RecordingStoreOwner(Store());
        var directory = Directory(store);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            directory.StartAsync(CancellationToken.None)));
        Assert.AreEqual(1, store.Reads);
        directory.Register(Registration(
            "terminal-sensitive",
            cwd,
            RuntimeNames.Codex));
        directory.ClaimById(
            "terminal-sensitive",
            cwd,
            RuntimeNames.Codex,
            "session-sensitive");

        var health = directory.ComponentHealth;
        Assert.AreEqual("ready", health.Status);
        StringAssert.Contains(health.Detail, "registrations=1");
        Assert.IsFalse(health.Detail.Contains("terminal-sensitive", StringComparison.Ordinal));
        Assert.IsFalse(health.Detail.Contains("sensitive-project-path", StringComparison.Ordinal));
        Assert.IsFalse(health.Detail.Contains("session-sensitive", StringComparison.Ordinal));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            directory.StopAsync(new CancellationToken(canceled: true))));
        Assert.IsFalse(directory.Snapshot.Initialized);
        Assert.ThrowsException<InvalidOperationException>(() =>
            directory.FindBySession("session-sensitive"));
        await directory.StartAsync(CancellationToken.None);
        Assert.AreEqual(2, store.Reads);
    }

    [TestMethod]
    public async Task ValidatesPublicRegistrationInputs()
    {
        var cwd = ProjectPath("validation");
        var directory = Directory(new RecordingStoreOwner(Store()));
        await directory.StartAsync(CancellationToken.None);

        Assert.ThrowsException<ArgumentException>(() =>
            directory.Register(new(null!, cwd, RuntimeNames.Codex, false, true)));
        Assert.ThrowsException<ArgumentException>(() =>
            directory.Register(new("short", cwd, RuntimeNames.Codex, false, true)));
        Assert.ThrowsException<ArgumentException>(() =>
            directory.Register(new("terminal-relative", "relative", RuntimeNames.Codex, false, true)));
        Assert.ThrowsException<ArgumentException>(() =>
            directory.Register(new("terminal-runtime", cwd, "invalid", false, true)));
        Assert.IsNull(directory.FindBySession(string.Empty));
    }

    private static ActiveManagedTerminalDirectory Directory(
        IBridgeProductionStoreOwner store,
        TimeProvider? timeProvider = null) => new(
            ActiveOptions(),
            store,
            timeProvider ?? new MutableTimeProvider(Origin));

    private static BridgeHostOptions ActiveOptions() => new(
        ProjectPath("data"),
        IPAddress.Loopback,
        0,
        BridgeOwnershipMode.Active,
        "managed-terminal-directory-test");

    private static BridgeManagedTerminalRegistration Registration(
        string terminalId,
        string cwd,
        string runtime) => new(
            terminalId,
            cwd,
            runtime,
            Elevated: false,
            Ready: true);

    private static string ProjectPath(string name) =>
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bridge-terminal-tests", name));

    private static NodeStoreSnapshot Store(params SessionStoreRecord[] sessions)
    {
        var document = new SessionStoreDocument();
        for (var index = 0; index < sessions.Length; index++)
        {
            document.Sessions[$"record-{index}"] = sessions[index];
        }
        return new(
            new BindingStoreDocument(),
            document,
            new RouteStoreDocument(),
            new ApprovalStoreDocument(),
            new SettingsStoreDocument(),
            new ControlTokenStoreDocument());
    }

    private static SessionStoreRecord Session(
        string sessionId,
        string cwd,
        string? runtime,
        string status,
        string source,
        string terminalId) => SessionWithExtension(
            sessionId,
            cwd,
            runtime,
            status,
            ("source", source),
            ("managedTerminalId", terminalId));

    private static SessionStoreRecord SessionWithExtension(
        string sessionId,
        string cwd,
        string? runtime,
        string status,
        params (string Name, object? Value)[] extensions) => new()
        {
            SessionId = sessionId,
            Cwd = cwd,
            Runtime = runtime,
            Status = status,
            LastSeenAt = Origin.ToString("O"),
            ExtensionData = extensions.ToDictionary(
                extension => extension.Name,
                extension => JsonSerializer.SerializeToElement(extension.Value),
                StringComparer.Ordinal),
        };

    private sealed class RecordingStoreOwner(NodeStoreSnapshot store)
        : IBridgeProductionStoreOwner
    {
        public bool IsOpen { get; set; } = true;
        public int Reads { get; private set; }
        public BridgeProductionStoreSnapshot Snapshot => new(
            IsOpen ? BridgeProductionStoreState.Open : BridgeProductionStoreState.NotOpened,
            null,
            0);

        public ValueTask OpenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<NodeStoreSnapshot> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            Reads++;
            return IsOpen
                ? ValueTask.FromResult(store)
                : ValueTask.FromException<NodeStoreSnapshot>(
                    new InvalidOperationException("生产 Store 尚未成功打开。"));
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask UpdateAsync(
            Func<NodeStoreSnapshot, NodeStoreSnapshot> update,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask CloseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan amount) => current += amount;

        public void Set(DateTimeOffset value) => current = value;
    }
}
