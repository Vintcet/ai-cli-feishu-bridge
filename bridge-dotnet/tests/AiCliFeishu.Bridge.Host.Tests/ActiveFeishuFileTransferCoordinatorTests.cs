using System.Net;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActiveFeishuFileTransferCoordinatorTests
{
    [TestMethod]
    public async Task InboundAttachmentsUseSafeNamesAndAreStagedPerChat()
    {
        var root = CreateTempDirectory();
        try
        {
            var gateway = new RecordingGateway(EncodingBytes("payload"));
            var store = new RecordingStore(CreateStore());
            var settings = Settings(
                Path.Combine(root, "uploads"),
                maxFiles: 20,
                maxBytes: 1024);
            using var coordinator = new ActiveFeishuFileTransferCoordinator(
                ActiveOptions(root),
                store,
                gateway,
                settings);

            var key = coordinator.AttachmentKey("owner", "chat-a");
            var saved = await coordinator.DownloadAndStageAsync(
                key,
                "message-1",
                [new(
                    "file",
                    "file-1",
                    $"{new string('a', 119)}.{new string('b', 20)}")]);

            Assert.AreEqual(1, saved.Count);
            Assert.IsTrue(saved[0].AbsolutePath.StartsWith(
                Path.GetFullPath(settings.UploadsDirectory),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal));
            Assert.IsTrue(saved[0].FileName.Length <= 120);
            Assert.IsFalse(saved[0].FileName.EndsWith('.') || saved[0].FileName.EndsWith(' '));
            Assert.AreEqual(1, coordinator.PeekAttachments(key).Count);
            Assert.AreEqual(0, coordinator.PeekAttachments(
                coordinator.AttachmentKey("owner", "chat-b")).Count);
            Assert.AreEqual(1, coordinator.TakeAttachments(key).Count);
            Assert.AreEqual(0, coordinator.TakeAttachments(key).Count);
            Assert.AreEqual(1, gateway.Downloads.Count);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task InboundGlobalLimitsRejectBeforeDownloadAndCleanPartialFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            var uploadRoot = Path.Combine(root, "uploads");
            var month = Path.Combine(uploadRoot, DateTimeOffset.UtcNow.ToString("yyyy-MM"));
            Directory.CreateDirectory(month);
            await File.WriteAllTextAsync(Path.Combine(month, "existing.bin"), "1234");

            var countGateway = new RecordingGateway(EncodingBytes("x"));
            using (var countCoordinator = new ActiveFeishuFileTransferCoordinator(
                       ActiveOptions(root),
                       new RecordingStore(CreateStore()),
                       countGateway,
                       Settings(uploadRoot, maxFiles: 1, maxBytes: 1024)))
            {
                await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
                    countCoordinator.DownloadAndStageAsync(
                        "owner\0chat",
                        "message-count",
                        [new("file", "file", "count.bin")]));
            }
            Assert.AreEqual(0, countGateway.Downloads.Count);

            var byteGateway = new RecordingGateway(EncodingBytes("12"));
            using (var byteCoordinator = new ActiveFeishuFileTransferCoordinator(
                       ActiveOptions(root),
                       new RecordingStore(CreateStore()),
                       byteGateway,
                       Settings(uploadRoot, maxFiles: 10, maxBytes: 5)))
            {
                var error = await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
                    byteCoordinator.DownloadAndStageAsync(
                        "owner\0chat",
                        "message-bytes",
                        [new("file", "file", "bytes.bin")]));
                StringAssert.Contains(error.Message, "总容量不能超过 5 B");
            }
            CollectionAssert.AreEqual(
                new[] { "existing.bin" },
                Directory.GetFiles(month).Select(Path.GetFileName).ToArray());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task InboundCoordinatorRejectsGatewaySizeAboveLocalLimit()
    {
        var root = CreateTempDirectory();
        try
        {
            var uploadRoot = Path.Combine(root, "uploads");
            var gateway = new RecordingGateway(
                EncodingBytes("payload"),
                reportedSize: 2_000);
            using var coordinator = new ActiveFeishuFileTransferCoordinator(
                ActiveOptions(root),
                new RecordingStore(CreateStore()),
                gateway,
                Settings(uploadRoot, maxFiles: 20, maxBytes: 10_000) with
                {
                    InboundFileMaxBytes = 1_000,
                });

            await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
                coordinator.DownloadAndStageAsync(
                    "owner\0chat",
                    "message-too-large",
                    [new("file", "file", "too-large.bin")]));
            Assert.AreEqual(
                0,
                Directory.GetFiles(
                    uploadRoot,
                    "*",
                    SearchOption.AllDirectories).Length);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void FileReturnDirectivesAreStrippedAndInstructionIsExplicit()
    {
        var parsed = BridgeFileTransferProtocol.ExtractDirectives(
            "报告已生成。\n\nBRIDGE_SEND_FILE: \"K:\\project\\report.txt\"\n" +
            "BRIDGE_SEND_FILE: K:\\project\\report.txt\n" +
            "BRIDGE_SEND_FILE: K:\\project\\second.pdf\n" +
            "BRIDGE_SEND_FILE: K:\\project\\third.zip\n" +
            "BRIDGE_SEND_FILE: K:\\project\\fourth.txt");

        Assert.AreEqual("报告已生成。", parsed.DisplayMessage);
        CollectionAssert.AreEqual(
            new[]
            {
                "K:\\project\\report.txt",
                "K:\\project\\second.pdf",
                "K:\\project\\third.zip",
            },
            parsed.Paths.ToArray());
        StringAssert.Contains(
            BridgeFileTransferProtocol.AddFileReturnInstruction("生成报告"),
            "BRIDGE_SEND_FILE: 绝对路径");
    }

    [TestMethod]
    public async Task OutboundFilesStayInsideCwdAndPersistStopRoutes()
    {
        var root = CreateTempDirectory();
        try
        {
            var project = Path.Combine(root, "project");
            Directory.CreateDirectory(project);
            var report = Path.Combine(project, "report.txt");
            await File.WriteAllTextAsync(report, "done");
            var outside = Path.Combine(root, "outside.txt");
            await File.WriteAllTextAsync(outside, "secret");

            var gateway = new RecordingGateway(EncodingBytes("unused"));
            var store = new RecordingStore(CreateStore(
                new SessionStoreRecord
                {
                    SessionId = "session-1",
                    Cwd = project,
                    Runtime = "codex",
                }));
            using var coordinator = new ActiveFeishuFileTransferCoordinator(
                ActiveOptions(root),
                store,
                gateway,
                Settings(Path.Combine(root, "uploads"), maxFiles: 20, maxBytes: 1024));

            var result = await coordinator.SendRequestedFilesAsync(
                "session-1",
                "chat-1",
                [report, outside, Path.Combine(project, "missing.txt")]);

            Assert.AreEqual(1, result.SentCount);
            Assert.AreEqual(2, result.FailedCount);
            Assert.AreEqual(1, gateway.SentFiles.Count);
            Assert.AreEqual("stop", store.Current.Routes.Messages["sent-file-1"].Kind);
            Assert.AreEqual("session-1", store.Current.Routes.Messages["sent-file-1"].SessionId);
            StringAssert.Contains(gateway.SentTexts.Single().Text, "失败 2 个");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void QueuedFileReturnWaitsForTheRequestedTurn()
    {
        var root = CreateTempDirectory();
        try
        {
            using var coordinator = new ActiveFeishuFileTransferCoordinator(
                ActiveOptions(root),
                new RecordingStore(CreateStore()),
                new RecordingGateway(EncodingBytes("unused")),
                Settings(Path.Combine(root, "uploads"), maxFiles: 20, maxBytes: 1024));

            coordinator.ObservePromptDispatch("session-1", "chat-1", false, queued: true);
            coordinator.ObservePromptDispatch("session-1", "chat-1", true, queued: true);

            Assert.IsNull(coordinator.AdvanceReturnRequest("session-1"));
            Assert.IsNull(coordinator.AdvanceReturnRequest("session-1"));
            Assert.AreEqual(
                "chat-1",
                coordinator.AdvanceReturnRequest("session-1")!.ChatId);
            Assert.IsNull(coordinator.AdvanceReturnRequest("session-1"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static ActiveFeishuFileTransferSettings Settings(
        string uploadRoot,
        int maxFiles,
        long maxBytes) => new(
        uploadRoot,
        1024,
        4,
        maxFiles,
        maxBytes,
        TimeSpan.FromHours(1),
        1024);

    private static BridgeHostOptions ActiveOptions(string root) => new(
        Path.Combine(root, "data"),
        IPAddress.Loopback,
        0,
        BridgeOwnershipMode.Active,
        "file-transfer-test");

    private static BridgeStoreSnapshot CreateStore(SessionStoreRecord? session = null) => new(
        new BindingStoreDocument(),
        new SessionStoreDocument
        {
            Sessions = session is null
                ? []
                : new Dictionary<string, SessionStoreRecord>(StringComparer.Ordinal)
                {
                    [session.SessionId] = session,
                },
        },
        new RouteStoreDocument(),
        new ApprovalStoreDocument(),
        new SettingsStoreDocument(),
        new ControlTokenStoreDocument());

    private static byte[] EncodingBytes(string value) =>
        System.Text.Encoding.UTF8.GetBytes(value);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"bridge-file-transfer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class RecordingStore(BridgeStoreSnapshot current) :
        IBridgeProductionStoreOwner
    {
        public BridgeStoreSnapshot Current { get; private set; } = current;

        public BridgeProductionStoreSnapshot Snapshot => new(
            BridgeProductionStoreState.Open,
            Current,
            6);

        public ValueTask OpenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<BridgeStoreSnapshot> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Current);
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask UpdateAsync(
            Func<BridgeStoreSnapshot, BridgeStoreSnapshot> update,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Current = update(Current);
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class RecordingGateway(byte[] data, long? reportedSize = null) : IFeishuGateway
    {
        public List<string> Downloads { get; } = [];

        public List<string> SentFiles { get; } = [];

        public List<(string ChatId, string Text)> SentTexts { get; } = [];

        public Task<string> SendTextAsync(
            string chatId,
            string text,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SentTexts.Add((chatId, text));
            return Task.FromResult($"text-{SentTexts.Count}");
        }

        public Task<string> ReplyTextAsync(
            string messageId,
            string text,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("reply");

        public Task<string> SendCardAsync(
            string chatId,
            FeishuCardView card,
            string? idempotencyKey = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("card");

        public Task PatchCardAsync(
            string messageId,
            FeishuCardView card,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<FeishuSessionGroup> CreateSessionGroupAsync(
            string ownerOpenId,
            string name,
            string description,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new FeishuSessionGroup("chat", name));

        public Task UpdateSessionGroupNameAsync(
            string chatId,
            string name,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteSessionGroupAsync(
            string chatId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async Task<long> DownloadMessageResourceAsync(
            string messageId,
            string fileKey,
            string resourceType,
            string destinationPath,
            long maxBytes,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Downloads.Add(destinationPath);
            await File.WriteAllBytesAsync(destinationPath, data, cancellationToken);
            return reportedSize ?? data.LongLength;
        }

        public Task<string> SendLocalFileAsync(
            string chatId,
            string filePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SentFiles.Add(filePath);
            return Task.FromResult($"sent-file-{SentFiles.Count}");
        }
    }
}
