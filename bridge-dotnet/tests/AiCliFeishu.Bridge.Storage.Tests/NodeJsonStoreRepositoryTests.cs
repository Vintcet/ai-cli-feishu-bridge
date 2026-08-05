using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishu.Bridge.Storage.Tests;

[TestClass]
public sealed class NodeJsonStoreRepositoryTests
{
    [TestMethod]
    public async Task LoadsNodeStoresAndProjectsCoreState()
    {
        await using var directory = await StoreTestDirectory.CreateAsync();
        var repository = new NodeJsonStoreRepository(directory.Path);

        var snapshot = await repository.LoadAsync();
        var core = NodeStoreCoreProjection.Project(snapshot);

        Assert.AreEqual(1, core.Sessions.Sessions.Count);
        Assert.AreEqual("opencode", core.Sessions.Sessions["session-1"].Runtime);
        Assert.AreEqual(1, core.Routes.Messages.Count);
        Assert.AreEqual(1, core.Approvals.Requests.Count);
    }

    [TestMethod]
    public async Task ReadOnlyRepositoryRejectsWrites()
    {
        await using var directory = await StoreTestDirectory.CreateAsync();
        var repository = new NodeJsonStoreRepository(directory.Path);
        var snapshot = await repository.LoadAsync();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => repository.WriteAsync(snapshot));
    }

    [TestMethod]
    public async Task CopyRoundTripPreservesUnknownFields()
    {
        await using var directory = await StoreTestDirectory.CreateAsync();
        var repository = new NodeJsonStoreRepository(
            directory.Path,
            NodeStoreAccess.ReadWriteCopy);
        var snapshot = await repository.LoadAsync();

        await repository.WriteAsync(snapshot);
        var rewritten = await repository.LoadAsync();

        Assert.AreEqual(
            "keep-root",
            rewritten.Sessions.ExtensionData!["futureRoot"].GetString());
        Assert.AreEqual(
            "keep-session",
            rewritten.Sessions.Sessions["session-1"]
                .ExtensionData!["futureSession"].GetString());
        Assert.AreEqual(
            "keep-approval",
            rewritten.Approvals.Requests["approval-1"]
                .ExtensionData!["futureApproval"].GetString());
        Assert.IsFalse(Directory.EnumerateFiles(directory.Path, "*.tmp").Any());
    }

    [TestMethod]
    public async Task InvalidStoreIsRejectedWithoutRenamingOrOverwritingIt()
    {
        await using var directory = await StoreTestDirectory.CreateAsync();
        var sessions = System.IO.Path.Combine(directory.Path, "sessions.json");
        await File.WriteAllTextAsync(sessions, "{\"sessions\":{\"bad\":{}}}");
        var before = await File.ReadAllTextAsync(sessions);

        await Assert.ThrowsExceptionAsync<NodeStoreValidationException>(
            () => new NodeJsonStoreRepository(directory.Path).LoadAsync());

        Assert.AreEqual(before, await File.ReadAllTextAsync(sessions));
        Assert.IsTrue(File.Exists(sessions));
        Assert.AreEqual(0, Directory.EnumerateFiles(directory.Path, "*.corrupt-*").Count());
    }

    private sealed class StoreTestDirectory : IAsyncDisposable
    {
        private StoreTestDirectory(string path) => Path = path;

        public string Path { get; }

        public static async Task<StoreTestDirectory> CreateAsync()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"ai-cli-feishu-m2-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            var files = new Dictionary<string, string>
            {
                ["bindings.json"] = """
                    {"users":{"owner":{"openId":"owner","chatId":"chat-owner","chatType":"p2p","boundAt":"2026-08-06T00:00:00Z"}},"ownerOpenId":"owner"}
                    """,
                ["sessions.json"] = """
                    {"sessions":{"session-1":{"sessionId":"session-1","shortId":"session1","cwd":"K:\\project","projectName":"project","status":"waiting","runtime":"opencode","openedAt":"2026-08-06T00:00:00Z","lastSeenAt":"2026-08-06T00:01:00Z","futureSession":"keep-session"}},"futureRoot":"keep-root"}
                    """,
                ["message-routes.json"] = """
                    {"messages":{"message-1":{"messageId":"message-1","sessionId":"session-1","chatId":"chat-1","kind":"approval","createdAt":"2026-08-06T00:01:00Z","requestId":"approval-1"}},"processedInbound":{"inbound-1":"2026-08-06T00:02:00Z"}}
                    """,
                ["approvals.json"] = """
                    {"requests":{"approval-1":{"requestId":"approval-1","sessionId":"session-1","turnId":"turn-1","cwd":"K:\\project","toolName":"shell_command","toolPreview":"summary","createdAt":"2026-08-06T00:01:00Z","expiresAt":"2026-08-06T00:11:00Z","status":"pending","messageIds":["message-1"],"futureApproval":"keep-approval"}}}
                    """,
                ["settings.json"] = """
                    {"workspaceRoot":"K:\\project","notifyActivity":false,"retryMaxAttempts":3,"futureSetting":true}
                    """,
                ["control-token.json"] = """
                    {"token":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}
                    """,
            };
            foreach (var file in files)
            {
                await File.WriteAllTextAsync(System.IO.Path.Combine(path, file.Key), file.Value);
            }
            return new StoreTestDirectory(path);
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(Path, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
