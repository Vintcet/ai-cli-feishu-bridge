namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class BridgeStoreViewTests
{
    private string? directory;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(Path.GetTempPath(), $"ai-cli-feishu-store-view-{Guid.NewGuid():N}");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task MissingStoreStaysMissingAndCreatesNoFiles()
    {
        var view = new ReadOnlyBridgeStoreView(BridgeHostOptions.Passive(directory!));

        await view.StartAsync(CancellationToken.None);

        Assert.AreEqual(BridgeStoreViewStatuses.Missing, view.Snapshot.Status);
        Assert.IsFalse(Directory.Exists(directory));
        Assert.AreEqual("missing", view.ComponentHealth.Detail);
    }

    [TestMethod]
    public async Task ValidStoreLoadsOnlyCoreProjectionAndRedactedCounts()
    {
        Directory.CreateDirectory(directory!);
        await File.WriteAllTextAsync(
            Path.Combine(directory!, "bindings.json"),
            """
            {"users":{"secret-owner":{"openId":"secret-owner","chatId":"secret-chat","chatType":"p2p","boundAt":"2026-08-06T00:00:00Z"}},"ownerOpenId":"secret-owner"}
            """);
        await File.WriteAllTextAsync(
            Path.Combine(directory!, "sessions.json"),
            """
            {"sessions":{"secret-session":{"sessionId":"secret-session","cwd":"K:\\secret-project","status":"waiting","runtime":"codex","openedAt":"2026-08-06T00:00:00Z","lastSeenAt":"2026-08-06T00:01:00Z"}}}
            """);
        var view = new ReadOnlyBridgeStoreView(BridgeHostOptions.Passive(directory!));

        await view.StartAsync(CancellationToken.None);

        Assert.AreEqual(BridgeStoreViewStatuses.Loaded, view.Snapshot.Status);
        Assert.AreEqual(1, view.Snapshot.Core!.Sessions.Sessions.Count);
        Assert.AreEqual(2, view.Snapshot.StoreFiles);
        Assert.AreEqual(1, view.Snapshot.Bindings);
        StringAssert.Contains(view.ComponentHealth.Detail!, "sessions=1");
        Assert.IsFalse(view.ComponentHealth.Detail!.Contains("secret", StringComparison.Ordinal));
        CollectionAssert.AreEquivalent(
            new[] { "bindings.json", "sessions.json" },
            Directory.EnumerateFiles(directory!).Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public async Task InvalidStoreIsReportedWithoutMutationOrSecretDetails()
    {
        Directory.CreateDirectory(directory!);
        var sessions = Path.Combine(directory!, "sessions.json");
        const string invalid = "{\"sessions\":{\"secret-session\":{}}}";
        await File.WriteAllTextAsync(sessions, invalid);
        var view = new ReadOnlyBridgeStoreView(BridgeHostOptions.Passive(directory!));

        await view.StartAsync(CancellationToken.None);

        Assert.AreEqual(BridgeStoreViewStatuses.Incompatible, view.Snapshot.Status);
        Assert.AreEqual("failed", view.ComponentHealth.Status);
        Assert.AreEqual("incompatible file=sessions.json", view.ComponentHealth.Detail);
        Assert.AreEqual(invalid, await File.ReadAllTextAsync(sessions));
        CollectionAssert.AreEqual(
            new[] { "sessions.json" },
            Directory.EnumerateFiles(directory!).Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public async Task InvalidJsonIsReportedWithoutLeakingParserOrPathDetails()
    {
        Directory.CreateDirectory(directory!);
        await File.WriteAllTextAsync(Path.Combine(directory!, "settings.json"), "{invalid");
        var view = new ReadOnlyBridgeStoreView(BridgeHostOptions.Passive(directory!));

        await view.StartAsync(CancellationToken.None);

        Assert.AreEqual(BridgeStoreViewStatuses.Incompatible, view.Snapshot.Status);
        Assert.AreEqual("incompatible file=json", view.ComponentHealth.Detail);
        Assert.IsFalse(view.ComponentHealth.Detail!.Contains(directory!, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task RefreshReplacesSnapshotWithoutCreatingOrMutatingStoreFiles()
    {
        var view = new ReadOnlyBridgeStoreView(BridgeHostOptions.Passive(directory!));
        await view.StartAsync(CancellationToken.None);
        Assert.AreEqual(BridgeStoreViewStatuses.Missing, view.Snapshot.Status);

        Directory.CreateDirectory(directory!);
        var sessions = Path.Combine(directory!, "sessions.json");
        const string source = """
            {"sessions":{"secret-session":{"sessionId":"secret-session","cwd":"K:\\secret","status":"waiting","lastSeenAt":"2026-08-06T00:00:00Z"}}}
            """;
        await File.WriteAllTextAsync(sessions, source);

        await view.RefreshAsync(CancellationToken.None);

        Assert.AreEqual(BridgeStoreViewStatuses.Loaded, view.Snapshot.Status);
        Assert.AreEqual(1, view.Snapshot.Core!.Sessions.Sessions.Count);
        Assert.AreEqual(source, await File.ReadAllTextAsync(sessions));
        CollectionAssert.AreEqual(
            new[] { "sessions.json" },
            Directory.EnumerateFiles(directory!).Select(Path.GetFileName).ToArray());
    }
}
