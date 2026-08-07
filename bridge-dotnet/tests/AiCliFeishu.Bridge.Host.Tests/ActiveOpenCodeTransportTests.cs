using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.OpenCode;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActiveOpenCodeTransportTests
{
    private const string SessionId = "session-active";
    private static readonly RuntimeCommandContext Context =
        new("command-opencode", "trace-opencode", "correlation-opencode");
    private static readonly string Cwd = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "opencode transport project"));

    [TestMethod]
    public async Task SendsScopedPromptInputAndAbortWithExpectedPayloads()
    {
        var (directory, _) = await ReadyDirectoryAsync(5_301);
        var handler = new QueueHandler();
        handler.Enqueue(HttpStatusCode.NoContent);
        handler.Enqueue(HttpStatusCode.OK);
        handler.Enqueue(HttpStatusCode.NoContent);
        using var client = new HttpClient(handler);
        var lifecycle = new RecordingLifecycle();
        using var transport = Transport(directory, lifecycle, client);

        await transport.SendPromptAsync(Context, SessionId, "hello");
        await transport.ResolveInputAsync(
            Context,
            SessionId,
            "question/1",
            new IReadOnlyList<string>[] { new[] { "alpha", "beta" } });
        await transport.StopAsync(Context, SessionId, "done");

        var requests = handler.Requests;
        Assert.AreEqual(3, requests.Count);
        Assert.AreEqual(
            $"/session/{SessionId}/prompt_async",
            requests[0].Uri.AbsolutePath);
        AssertScopedToDirectory(requests[0].Uri);
        using (var body = JsonDocument.Parse(requests[0].Body!))
        {
            var part = body.RootElement.GetProperty("parts")[0];
            Assert.AreEqual("text", part.GetProperty("type").GetString());
            Assert.AreEqual("hello", part.GetProperty("text").GetString());
        }
        Assert.AreEqual("/question/question%2F1/reply", requests[1].Uri.AbsolutePath);
        AssertScopedToDirectory(requests[1].Uri);
        using (var body = JsonDocument.Parse(requests[1].Body!))
        {
            CollectionAssert.AreEqual(
                new[] { "alpha", "beta" },
                body.RootElement.GetProperty("answers")[0]
                    .EnumerateArray()
                    .Select(value => value.GetString())
                    .ToArray());
        }
        Assert.AreEqual($"/session/{SessionId}/abort", requests[2].Uri.AbsolutePath);
        Assert.IsNull(requests[2].Body);
        CollectionAssert.AreEqual(
            new[] { $"stop:{SessionId}:done" },
            lifecycle.Calls.ToArray());
    }

    [TestMethod]
    public async Task ApprovalUsesNodeCompatibleFallbackOrderAndDecisionMapping()
    {
        var (directory, _) = await ReadyDirectoryAsync(5_302);
        var handler = new QueueHandler();
        handler.Enqueue(HttpStatusCode.NotFound);
        handler.Enqueue(HttpStatusCode.MethodNotAllowed);
        handler.Enqueue(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        using var transport = Transport(directory, new RecordingLifecycle(), client);

        await transport.ResolveApprovalAsync(
            Context,
            SessionId,
            "permission/1",
            "allow_session");

        var requests = handler.Requests;
        Assert.AreEqual(3, requests.Count);
        Assert.AreEqual(
            $"/api/session/{SessionId}/permission/permission%2F1/reply",
            requests[0].Uri.AbsolutePath);
        Assert.AreEqual(string.Empty, requests[0].Uri.Query);
        Assert.AreEqual(
            "/permission/permission%2F1/reply",
            requests[1].Uri.AbsolutePath);
        Assert.AreEqual(
            $"/session/{SessionId}/permissions/permission%2F1",
            requests[2].Uri.AbsolutePath);
        AssertScopedToDirectory(requests[1].Uri);
        AssertScopedToDirectory(requests[2].Uri);
        Assert.AreEqual("always", JsonValue(requests[0].Body!, "reply"));
        Assert.AreEqual("always", JsonValue(requests[1].Body!, "reply"));
        Assert.AreEqual("always", JsonValue(requests[2].Body!, "response"));
    }

    [TestMethod]
    public async Task ApprovalStopsOnNonCompatibilityErrorWithoutReadingItsBody()
    {
        var (directory, _) = await ReadyDirectoryAsync(5_303);
        var handler = new QueueHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, "sensitive-response-body");
        using var client = new HttpClient(handler);
        using var transport = Transport(directory, new RecordingLifecycle(), client);

        var error = await Assert.ThrowsExceptionAsync<HttpRequestException>(() =>
            transport.ResolveApprovalAsync(
                Context,
                SessionId,
                "permission-1",
                "deny"));

        Assert.AreEqual(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.IsFalse(error.Message.Contains(
            "sensitive-response-body",
            StringComparison.Ordinal));
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task GenerationReplacementAfterFirstResponsePreventsFallback()
    {
        var (directory, identity) = await ReadyDirectoryAsync(5_304);
        var handler = new QueueHandler();
        handler.Enqueue((_, _) =>
        {
            directory.Register(identity.Port, Path.Combine(Cwd, "replacement"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        handler.Enqueue(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        using var transport = Transport(directory, new RecordingLifecycle(), client);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            transport.ResolveApprovalAsync(
                Context,
                SessionId,
                "permission-2",
                "allow_once"));

        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task SessionTransferWhileRequestIsInFlightRejectsCompletion()
    {
        var (directory, first) = await ReadyDirectoryAsync(5_305);
        var second = directory.Register(5_306, Path.Combine(Cwd, "second"));
        directory.SetReady(second.Port, second.Generation, true);
        var entered = Signal();
        var release = Signal();
        var handler = new QueueHandler();
        handler.Enqueue(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        using var transport = Transport(directory, new RecordingLifecycle(), client);

        var sending = transport.SendPromptAsync(Context, SessionId, "in flight");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(directory.RememberSession(
            second.Port,
            second.Generation,
            SessionId));
        Assert.IsFalse(directory.IsCurrent(first, SessionId));
        release.TrySetResult();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => sending);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task AppliesPerOperationTimeoutWithoutMaskingCallerCancellation()
    {
        var (directory, _) = await ReadyDirectoryAsync(5_307);
        var handler = new QueueHandler();
        handler.Enqueue(BlockUntilCancelledAsync);
        handler.Enqueue(BlockUntilCancelledAsync);
        handler.Enqueue(BlockUntilCancelledAsync);
        using var client = new HttpClient(handler);
        using var transport = Transport(
            directory,
            new RecordingLifecycle(),
            client,
            promptTimeout: TimeSpan.FromMilliseconds(30),
            commandTimeout: TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
            transport.SendPromptAsync(Context, SessionId, "timeout"));
        await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
            transport.ResolveInputAsync(
                Context,
                SessionId,
                "question-timeout",
                Array.Empty<IReadOnlyList<string>>()));
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() =>
            transport.SendPromptAsync(
                Context,
                SessionId,
                "caller cancellation",
                cancellation.Token));

        Assert.AreEqual(3, handler.Requests.Count);
    }

    [TestMethod]
    public async Task LaunchAndResumeDelegateOnlyLifecycleWorkBeforeOptionalPrompt()
    {
        var directory = new RecordingDirectory
        {
            Target = new(5_308, Cwd, 1, Ready: false),
        };
        var lifecycle = new RecordingLifecycle();
        using var client = new HttpClient(new QueueHandler());
        using var transport = Transport(directory, lifecycle, client);

        await transport.LaunchAsync(
            Context,
            "session-launch",
            Cwd,
            prompt: null,
            elevated: true);
        await transport.ResumeAsync(Context, SessionId, prompt: null);
        directory.Target = null;
        await transport.StopAsync(Context, "session-missing", "cleanup");

        CollectionAssert.AreEqual(
            new[]
            {
                $"launch:session-launch:{Cwd}:True",
                "wait:session-launch",
                $"resume:{SessionId}:{Cwd}",
                $"wait:{SessionId}",
                "stop:session-missing:cleanup",
            },
            lifecycle.Calls.ToArray());
    }

    [TestMethod]
    public async Task ReadinessModeTargetValidationAndDisposalFailClosedBeforeRequest()
    {
        var directory = new RecordingDirectory
        {
            Target = new(5_309, Cwd, 1, Ready: true),
        };
        var handler = new QueueHandler();
        using var client = new HttpClient(handler);
        var lifecycle = new RecordingLifecycle();
        using var transport = Transport(directory, lifecycle, client);

        Assert.IsTrue(transport.IsReady(SessionId));
        directory.Current = false;
        Assert.IsFalse(transport.IsReady(SessionId));
        directory.Current = true;
        directory.Target = new(0, Cwd, 1, Ready: true);
        Assert.ThrowsException<InvalidOperationException>(() =>
            transport.IsReady(SessionId));

        using var passive = new ActiveOpenCodeTransport(
            BridgeHostOptions.Passive(Path.GetTempPath(), port: 0),
            directory,
            lifecycle,
            client);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            passive.SendPromptAsync(Context, SessionId, "passive"));

        transport.Dispose();
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() =>
            transport.SendPromptAsync(Context, SessionId, "disposed"));
        Assert.AreEqual(0, handler.Requests.Count);
    }

    private static ActiveOpenCodeTransport Transport(
        IBridgeOpenCodeEndpointRegistrationDirectory directory,
        IOpenCodeRuntimeLifecycle lifecycle,
        HttpClient client,
        TimeSpan? promptTimeout = null,
        TimeSpan? commandTimeout = null) => new(
            ActiveOptions(),
            directory,
            lifecycle,
            client,
            promptTimeout,
            commandTimeout);

    private static async Task<(
        ActiveOpenCodeEndpointDirectory Directory,
        BridgeOpenCodeEndpointIdentity Identity)> ReadyDirectoryAsync(int port)
    {
        var directory = new ActiveOpenCodeEndpointDirectory(ActiveOptions());
        await directory.StartAsync(CancellationToken.None);
        var identity = directory.Register(port, Cwd);
        Assert.IsTrue(directory.SetReady(port, identity.Generation, true));
        Assert.IsTrue(directory.RememberSession(
            port,
            identity.Generation,
            SessionId));
        return (
            directory,
            directory.FindRegistrationBySession(SessionId)!);
    }

    private static BridgeHostOptions ActiveOptions() => new(
        Path.GetTempPath(),
        IPAddress.Loopback,
        0,
        BridgeOwnershipMode.Active,
        "active-opencode-transport-test");

    private static void AssertScopedToDirectory(Uri uri) =>
        Assert.AreEqual($"?directory={Uri.EscapeDataString(Cwd)}", uri.Query);

    private static string? JsonValue(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(propertyName).GetString();
    }

    private static async Task<HttpResponseMessage> BlockUntilCancelledAsync(
        CapturedRequest _,
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        string? Body);

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<
            Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>>>
            responses = new();
        private readonly ConcurrentQueue<CapturedRequest> requests = new();

        public IReadOnlyList<CapturedRequest> Requests => requests.ToArray();

        public void Enqueue(HttpStatusCode status, string? body = null) =>
            Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = body is null ? null : new StringContent(body),
            }));

        public void Enqueue(
            Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>>
                response) => responses.Enqueue(response);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var captured = new CapturedRequest(
                request.Method,
                request.RequestUri!,
                request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken));
            requests.Enqueue(captured);
            if (!responses.TryDequeue(out var response))
            {
                throw new InvalidOperationException("未配置 HTTP 测试响应。");
            }
            return await response(captured, cancellationToken);
        }
    }

    private sealed class RecordingLifecycle : IOpenCodeRuntimeLifecycle
    {
        public List<string> Calls { get; } = [];

        public Task LaunchAsync(
            RuntimeCommandContext context,
            string requestedExternalId,
            string cwd,
            bool elevated,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"launch:{requestedExternalId}:{cwd}:{elevated}");
            return Task.CompletedTask;
        }

        public Task ResumeAsync(
            RuntimeCommandContext context,
            string sessionExternalId,
            string? cwd,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"resume:{sessionExternalId}:{cwd}");
            return Task.CompletedTask;
        }

        public Task WaitUntilReadyAsync(
            RuntimeCommandContext context,
            string sessionExternalId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"wait:{sessionExternalId}");
            return Task.CompletedTask;
        }

        public Task StopAsync(
            RuntimeCommandContext context,
            string sessionExternalId,
            string? reason,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"stop:{sessionExternalId}:{reason}");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDirectory :
        IBridgeOpenCodeEndpointRegistrationDirectory
    {
        public BridgeOpenCodeEndpointDirectorySnapshot Snapshot { get; } =
            new(true, 0, 0, 0, 0);
        public BridgeOpenCodeEndpointIdentity? Target { get; set; }
        public bool Current { get; set; } = true;

        public BridgeOpenCodeEndpointIdentity? FindRegistrationBySession(
            string sessionExternalId) => Target;
        public bool IsCurrent(
            BridgeOpenCodeEndpointIdentity identity,
            string sessionExternalId) => Current && Equals(Target, identity);
        public BridgeOpenCodeEndpointIdentity Register(int port, string cwd) =>
            throw new NotSupportedException();
        public BridgeOpenCodeEndpointIdentity? TryRegisterAvailable(
            int port,
            string cwd) => throw new NotSupportedException();
        public bool Unregister(int port) => false;
        public bool Unregister(int port, long generation) => false;
        public bool SetReady(int port, long generation, bool ready) => false;
        public bool RememberSession(
            int port,
            long generation,
            string sessionExternalId) => false;
        public bool ForgetSession(
            int port,
            long generation,
            string sessionExternalId) => false;
        public IReadOnlyList<BridgeOpenCodeEndpointIdentity> ListRegistrations() => [];
        public ValueTask<long> WaitForChangeAsync(
            long observedRevision,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(observedRevision);
    }
}
