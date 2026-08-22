using System.Net;
using System.Text.Json.Nodes;
using AiCliFeishu.Bridge.Adapters.Feishu;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActiveFeishuGatewayTests
{
    [TestMethod]
    public void ProductionConstructionAndDisposalDoNotReadCredentials()
    {
        var credentials = new RecordingCredentialSource();

        using (var gateway = new ActiveFeishuGateway(
            ActiveOptions(),
            credentials))
        {
            Assert.AreEqual(0, credentials.Reads);
        }

        Assert.AreEqual(0, credentials.Reads);
    }

    [TestMethod]
    public async Task BuildsOnceAndForwardsEveryGatewayOperation()
    {
        var credentials = new RecordingCredentialSource();
        var inner = new RecordingGateway();
        var factories = 0;
        using var gateway = new ActiveFeishuGateway(
            ActiveOptions(),
            credentials,
            value =>
            {
                factories++;
                Assert.AreSame(credentials.Value, value);
                return inner;
            });
        var card = new FeishuCardView(new JsonObject { ["type"] = "card" });
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;

        Assert.AreEqual("sent-text", await gateway.SendTextAsync("chat-1", "text", token));
        Assert.AreEqual(
            "replied-text",
            await gateway.ReplyTextAsync("message-1", "reply", token));
        Assert.AreEqual(
            "sent-card",
            await gateway.SendCardAsync("chat-2", card, "idempotency-1", token));
        await gateway.PatchCardAsync("message-2", card, token);
        Assert.AreEqual(
            new FeishuSessionGroup("chat-group", "group-name"),
            await gateway.CreateSessionGroupAsync(
                "open-owner",
                "group-name",
                "description",
                token));
        await gateway.UpdateSessionGroupNameAsync("chat-group", "renamed", token);
        await gateway.DeleteSessionGroupAsync("chat-delete", token);
        Assert.AreEqual(
            42,
            await gateway.DownloadMessageResourceAsync(
                "message-3",
                "file-key",
                "file",
                "destination.bin",
                1_024,
                token));
        Assert.AreEqual(
            "sent-file",
            await gateway.SendLocalFileAsync("chat-3", "local.bin", token));

        Assert.AreEqual(1, credentials.Reads);
        Assert.AreEqual(1, factories);
        Assert.AreEqual(9, inner.Calls.Count);
        Assert.IsTrue(inner.Calls.All(call => call.CancellationToken == token));
        AssertCall(inner.Calls[0], "send-text", "chat-1", "text");
        AssertCall(inner.Calls[1], "reply-text", "message-1", "reply");
        AssertCall(inner.Calls[2], "send-card", "chat-2", card, "idempotency-1");
        AssertCall(inner.Calls[3], "patch-card", "message-2", card);
        AssertCall(
            inner.Calls[4],
            "create-group",
            "open-owner",
            "group-name",
            "description");
        AssertCall(inner.Calls[5], "update-group", "chat-group", "renamed");
        AssertCall(inner.Calls[6], "delete-group", "chat-delete");
        AssertCall(
            inner.Calls[7],
            "download",
            "message-3",
            "file-key",
            "file",
            "destination.bin",
            1_024L);
        AssertCall(inner.Calls[8], "send-file", "chat-3", "local.bin");
    }

    [TestMethod]
    public async Task HighPriorityMessageJumpsAheadOfQueuedLowPriorityMessages()
    {
        var inner = new RecordingGateway { BlockFirstCard = true };
        using var gateway = new ActiveFeishuGateway(
            ActiveOptions(),
            new RecordingCredentialSource(),
            _ => inner);
        var card = new FeishuCardView(new JsonObject { ["type"] = "card" });

        var first = gateway.SendCardAsync(
            "chat",
            card,
            "first",
            FeishuMessagePriority.Low);
        await inner.FirstCardStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = gateway.SendCardAsync(
            "chat",
            card,
            "second",
            FeishuMessagePriority.Low);
        var urgent = gateway.SendCardAsync(
            "chat",
            card,
            "urgent",
            FeishuMessagePriority.High);

        inner.ReleaseFirstCard.TrySetResult();
        await Task.WhenAll(first, second, urgent);

        CollectionAssert.AreEqual(
            new[] { "first", "urgent", "second" },
            inner.CardOrder.ToArray());
    }

    [TestMethod]
    public void CancelledOperationDoesNotLoadCredentialsOrGateway()
    {
        var credentials = new RecordingCredentialSource();
        var factories = 0;
        using var gateway = new ActiveFeishuGateway(
            ActiveOptions(),
            credentials,
            _ =>
            {
                factories++;
                return new RecordingGateway();
            });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            gateway.SendTextAsync("chat", "text", cancellation.Token));

        Assert.AreEqual(0, credentials.Reads);
        Assert.AreEqual(0, factories);
    }

    [TestMethod]
    public void RejectsPassiveOptionsBeforeReadingCredentials()
    {
        var credentials = new RecordingCredentialSource();
        var factories = 0;
        using var gateway = new ActiveFeishuGateway(
            BridgeHostOptions.Passive(Path.GetTempPath()),
            credentials,
            _ =>
            {
                factories++;
                return new RecordingGateway();
            });

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            gateway.SendTextAsync("chat", "text"));

        StringAssert.Contains(error.Message, "只能用于 Active Host");
        Assert.AreEqual(0, credentials.Reads);
        Assert.AreEqual(0, factories);
    }

    [TestMethod]
    public void DisposedGatewayRejectsOperationBeforeReadingCredentials()
    {
        var credentials = new RecordingCredentialSource();
        var gateway = new ActiveFeishuGateway(
            ActiveOptions(),
            credentials,
            _ => new RecordingGateway());

        gateway.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            gateway.SendTextAsync("chat", "text"));
        Assert.AreEqual(0, credentials.Reads);
    }

    [TestMethod]
    public void CreatesGatewayOptionsWithoutDiagnosticCredentialLeak()
    {
        var credentials = new BridgeFeishuCredentials(
            "cli_gateway",
            "gateway-secret");

        var options = ActiveFeishuGateway.CreateOptions(credentials);

        Assert.AreEqual(credentials.AppId, options.AppId);
        Assert.AreEqual(credentials.AppSecret, options.AppSecret);
        Assert.AreEqual(new Uri("https://open.feishu.cn/"), options.BaseUri);
        Assert.IsNull(options.TokenRefreshSkew);
        Assert.IsFalse(options.ToString().Contains(
            credentials.AppId,
            StringComparison.Ordinal));
        Assert.IsFalse(options.ToString().Contains(
            credentials.AppSecret,
            StringComparison.Ordinal));
    }

    private static void AssertCall(
        GatewayCall call,
        string method,
        params object?[] arguments)
    {
        Assert.AreEqual(method, call.Method);
        CollectionAssert.AreEqual(arguments, call.Arguments.ToArray());
    }

    private static BridgeHostOptions ActiveOptions() => new(
        Path.Combine(
            Path.GetTempPath(),
            $"bridge-active-feishu-gateway-{Guid.NewGuid():N}",
            "data"),
        IPAddress.Loopback,
        8765,
        BridgeOwnershipMode.Active,
        "feishu-gateway-test");

    private sealed class RecordingCredentialSource : IBridgeFeishuCredentialSource
    {
        public BridgeFeishuCredentials Value { get; } =
            new("cli_recording", "recording-secret");

        public int Reads { get; private set; }

        public BridgeFeishuCredentials Credentials
        {
            get
            {
                Reads++;
                return Value;
            }
        }
    }

    private sealed record GatewayCall(
        string Method,
        IReadOnlyList<object?> Arguments,
        CancellationToken CancellationToken);

    private sealed class RecordingGateway : IFeishuGateway
    {
        public List<GatewayCall> Calls { get; } = [];
        public List<string> CardOrder { get; } = [];
        public TaskCompletionSource FirstCardStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstCard { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockFirstCard { get; set; }
        private int cardCalls;

        public Task<string> SendTextAsync(
            string chatId,
            string text,
            CancellationToken cancellationToken = default)
        {
            Record("send-text", cancellationToken, chatId, text);
            return Task.FromResult("sent-text");
        }

        public Task<string> ReplyTextAsync(
            string messageId,
            string text,
            CancellationToken cancellationToken = default)
        {
            Record("reply-text", cancellationToken, messageId, text);
            return Task.FromResult("replied-text");
        }

        public async Task<string> SendCardAsync(
            string chatId,
            FeishuCardView card,
            string? idempotencyKey = null,
            CancellationToken cancellationToken = default)
        {
            Record("send-card", cancellationToken, chatId, card, idempotencyKey);
            var cardCall = Interlocked.Increment(ref cardCalls);
            if (BlockFirstCard && cardCall == 1)
            {
                FirstCardStarted.TrySetResult();
                await ReleaseFirstCard.Task.WaitAsync(cancellationToken);
            }
            lock (CardOrder)
            {
                CardOrder.Add(idempotencyKey ?? string.Empty);
            }
            return "sent-card";
        }

        public Task PatchCardAsync(
            string messageId,
            FeishuCardView card,
            CancellationToken cancellationToken = default)
        {
            Record("patch-card", cancellationToken, messageId, card);
            return Task.CompletedTask;
        }

        public Task<FeishuSessionGroup> CreateSessionGroupAsync(
            string ownerOpenId,
            string name,
            string description,
            CancellationToken cancellationToken = default)
        {
            Record(
                "create-group",
                cancellationToken,
                ownerOpenId,
                name,
                description);
            return Task.FromResult(new FeishuSessionGroup("chat-group", name));
        }

        public Task UpdateSessionGroupNameAsync(
            string chatId,
            string name,
            CancellationToken cancellationToken = default)
        {
            Record("update-group", cancellationToken, chatId, name);
            return Task.CompletedTask;
        }

        public Task DeleteSessionGroupAsync(
            string chatId,
            CancellationToken cancellationToken = default)
        {
            Record("delete-group", cancellationToken, chatId);
            return Task.CompletedTask;
        }

        public Task<long> DownloadMessageResourceAsync(
            string messageId,
            string fileKey,
            string resourceType,
            string destinationPath,
            long maxBytes,
            CancellationToken cancellationToken = default)
        {
            Record(
                "download",
                cancellationToken,
                messageId,
                fileKey,
                resourceType,
                destinationPath,
                maxBytes);
            return Task.FromResult(42L);
        }

        public Task<string> SendLocalFileAsync(
            string chatId,
            string filePath,
            CancellationToken cancellationToken = default)
        {
            Record("send-file", cancellationToken, chatId, filePath);
            return Task.FromResult("sent-file");
        }

        private void Record(
            string method,
            CancellationToken cancellationToken,
            params object?[] arguments) => Calls.Add(
                new GatewayCall(method, arguments, cancellationToken));
    }
}
