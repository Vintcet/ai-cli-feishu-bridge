using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishuControl;

[TestClass]
public sealed class BridgeHostCutoverProcessOperationsTests
{
    private const string ControlToken = "test-control-token";
    private const int NodeProcessId = 400;
    private const int DotNetProcessId = 500;
    private const string DotNetInstanceName = "production-dotnet";

    [DataTestMethod]
    [DataRow("https://127.0.0.1:18876/")]
    [DataRow("http://example.com:18876/")]
    [DataRow("http://127.0.0.1:18876/base/")]
    [DataRow("http://user@127.0.0.1:18876/")]
    public void OptionsRejectEndpointsOutsideTheExactLoopbackHttpOrigin(string endpoint)
    {
        Assert.ThrowsException<ArgumentException>(() =>
            Options(new StubStoreHandoffInspector(), endpoint: new(endpoint)).Validate());
    }

    [TestMethod]
    public async Task NodeIdentityMismatchDoesNotSendAStopRequest()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueIdentity(Node(NodeProcessId + 1));
        using var operations = Operations(handler);

        var error = await Assert.ThrowsExceptionAsync<BridgeHostCutoverOperationException>(() =>
            operations.RequestNodeStopAsync(Node(NodeProcessId), default).AsTask());

        Assert.AreEqual(BridgeCutoverFailureReason.NodeIdentityMismatch, error.Reason);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual(HttpMethod.Get, handler.Requests[0].Method);
        Assert.AreEqual("/health", handler.Requests[0].Path);
    }

    [TestMethod]
    public async Task NodeStopAuthenticatesThenSendsAllIdentityHeaders()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueIdentity(Node(NodeProcessId));
        handler.Enqueue(HttpStatusCode.Accepted);
        using var operations = Operations(handler);

        await operations.RequestNodeStopAsync(Node(NodeProcessId), default);

        Assert.AreEqual(2, handler.Requests.Count);
        var request = handler.Requests[1];
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("/control/shutdown", request.Path);
        Assert.AreEqual(ControlToken, request.Headers["X-AI-CLI-Feishu-Control-Token"]);
        Assert.AreEqual("node", request.Headers["X-AI-CLI-Feishu-Expected-Host-Kind"]);
        Assert.AreEqual("1", request.Headers["X-AI-CLI-Feishu-Management-Api-Version"]);
        Assert.AreEqual(NodeProcessId.ToString(), request.Headers["X-AI-CLI-Feishu-Expected-Process-Id"]);
    }

    [TestMethod]
    public async Task UnknownStopResultMapsToOwnershipUncertain()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueIdentity(Node(NodeProcessId));
        handler.EnqueueException(new HttpRequestException("connection closed"));
        using var operations = Operations(handler);

        var error = await Assert.ThrowsExceptionAsync<BridgeHostCutoverOperationException>(() =>
            operations.RequestNodeStopAsync(Node(NodeProcessId), default).AsTask());

        Assert.AreEqual(BridgeCutoverFailureReason.OwnershipUncertain, error.Reason);
        Assert.IsInstanceOfType<HttpRequestException>(error.InnerException);
    }

    [TestMethod]
    public async Task OfflineVerificationRejectsAnAuthenticatedReplacementPid()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueIdentity(Node(NodeProcessId + 1));
        using var operations = Operations(handler, maxProbeAttempts: 1);

        var error = await Assert.ThrowsExceptionAsync<BridgeHostCutoverOperationException>(() =>
            operations.VerifyNodeOfflineAsync(NodeProcessId, default).AsTask());

        Assert.AreEqual(BridgeCutoverFailureReason.OwnershipUncertain, error.Reason);
        StringAssert.Contains(error.Message, "身份替换");
    }

    [TestMethod]
    public async Task OfflineVerificationRejectsAStatusThatIsNotTheExpectedActiveOwner()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueIdentity(Node(NodeProcessId) with
        {
            OwnershipMode = "passive",
            ActiveOwner = false,
        });
        handler.EnqueueJson(new { ok = true });
        using var operations = Operations(handler, maxProbeAttempts: 1);

        var error = await Assert.ThrowsExceptionAsync<BridgeHostCutoverOperationException>(() =>
            operations.VerifyNodeOfflineAsync(NodeProcessId, default).AsTask());

        Assert.AreEqual(BridgeCutoverFailureReason.OwnershipUncertain, error.Reason);
    }

    [TestMethod]
    public async Task OfflineVerificationTreatsAnyUnknownHttpResponseAsAnOccupiedEndpoint()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        using var operations = Operations(handler, maxProbeAttempts: 1);

        var error = await Assert.ThrowsExceptionAsync<BridgeHostCutoverOperationException>(() =>
            operations.VerifyNodeOfflineAsync(NodeProcessId, default).AsTask());

        Assert.AreEqual(BridgeCutoverFailureReason.OwnershipUncertain, error.Reason);
        Assert.AreEqual(2, handler.Requests.Count);
    }

    [TestMethod]
    public async Task OfflineVerificationTreatsAHealthTimeoutAsAnOccupiedEndpoint()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueException(new TaskCanceledException("identity timeout"));
        handler.EnqueueException(new TaskCanceledException("public health timeout"));
        using var operations = Operations(handler, maxProbeAttempts: 1);

        var error = await Assert.ThrowsExceptionAsync<BridgeHostCutoverOperationException>(() =>
            operations.VerifyNodeOfflineAsync(NodeProcessId, default).AsTask());

        Assert.AreEqual(BridgeCutoverFailureReason.OwnershipUncertain, error.Reason);
        Assert.AreEqual(2, handler.Requests.Count);
    }

    [TestMethod]
    public async Task DotNetVerificationRejectsPidAndInstanceMismatches()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueIdentity(DotNet(DotNetProcessId + 1));
        using var operations = Operations(handler, maxProbeAttempts: 1);

        var error = await Assert.ThrowsExceptionAsync<BridgeHostCutoverOperationException>(() =>
            operations.VerifyDotNetActiveAsync(
                DotNetProcessId,
                DotNetInstanceName,
                default).AsTask());

        Assert.AreEqual(BridgeCutoverFailureReason.DotNetIdentityMismatch, error.Reason);
    }

    [TestMethod]
    public async Task RecoveryDotNetStopRequiresTheFullExpectedIdentity()
    {
        var expected = DotNet(DotNetProcessId);
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueIdentity(expected);
        handler.Enqueue(HttpStatusCode.Accepted);
        using var operations = Operations(handler);

        await operations.RequestExpectedDotNetStopAsync(expected, default);

        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual(HttpMethod.Post, handler.Requests[1].Method);
        Assert.AreEqual("/control/shutdown", handler.Requests[1].Path);
    }

    [TestMethod]
    public async Task RecoveryDotNetStopRefusesAnInstanceMismatchBeforeShutdown()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueIdentity(DotNet(DotNetProcessId) with
        {
            InstanceName = "replacement-dotnet",
        });
        using var operations = Operations(handler);

        var error = await Assert.ThrowsExceptionAsync<BridgeHostCutoverOperationException>(
            () => operations.RequestExpectedDotNetStopAsync(
                DotNet(DotNetProcessId),
                default).AsTask());

        Assert.AreEqual(BridgeCutoverFailureReason.OwnershipUncertain, error.Reason);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual(HttpMethod.Get, handler.Requests[0].Method);
    }

    [TestMethod]
    public async Task StoreHandoffEvidenceIsPassedThroughWithoutHttpCalls()
    {
        var evidence = new BridgeStoreHandoffEvidence(
            StoreFlushed: true,
            StoreCompatible: false,
            BridgeCutoverLeaseState.Invalid);
        var inspector = new StubStoreHandoffInspector(evidence);
        var handler = new QueueHttpMessageHandler();
        using var operations = Operations(handler, inspector: inspector);

        var actual = await operations.InspectStoreHandoffAsync(default);

        Assert.AreSame(evidence, actual);
        Assert.AreEqual(1, inspector.Calls);
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task StartReturnsTheActualProcessIdAndForcesHiddenDirectExecution()
    {
        ProcessStartInfo? recorded = null;
        var options = Options(
            new StubStoreHandoffInspector(),
            startProcess: startInfo =>
            {
                recorded = startInfo;
                return Process.GetCurrentProcess();
            });
        using var operations = new BridgeHostCutoverProcessOperations(options);

        var processId = await operations.StartDotNetActiveAsync(
            DotNetInstanceName,
            default);

        Assert.AreEqual(Environment.ProcessId, processId);
        Assert.IsNotNull(recorded);
        Assert.IsFalse(recorded.UseShellExecute);
        Assert.IsTrue(recorded.CreateNoWindow);
        Assert.AreEqual(ProcessWindowStyle.Hidden, recorded.WindowStyle);
    }

    [TestMethod]
    public async Task PersistentStartCallbackReceivesPidBeforeLaunchReturns()
    {
        var calls = new List<string>();
        var options = Options(
            new StubStoreHandoffInspector(),
            startProcess: _ =>
            {
                calls.Add("process.start");
                return Process.GetCurrentProcess();
            });
        using var operations = new BridgeHostCutoverProcessOperations(options);

        var processId = await operations.StartDotNetActiveAndBindAsync(
            DotNetInstanceName,
            (startedProcessId, cancellationToken) =>
            {
                Assert.AreEqual(Environment.ProcessId, startedProcessId);
                Assert.AreEqual(CancellationToken.None, cancellationToken);
                calls.Add("checkpoint.bind");
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);
        calls.Add("launch.return");

        Assert.AreEqual(Environment.ProcessId, processId);
        CollectionAssert.AreEqual(
            new[] { "process.start", "checkpoint.bind", "launch.return" },
            calls);
    }

    [TestMethod]
    public async Task AuthorizedDotNetStartAppendsExactCutoverOperation()
    {
        ProcessStartInfo? recorded = null;
        var options = Options(
            new StubStoreHandoffInspector(),
            startProcess: startInfo =>
            {
                recorded = startInfo;
                return Process.GetCurrentProcess();
            });
        using var operations = new BridgeHostCutoverProcessOperations(options);

        var processId = await operations.StartDotNetActiveAuthorizedAsync(
            DotNetInstanceName,
            "operation-a",
            default);

        Assert.AreEqual(Environment.ProcessId, processId);
        Assert.IsNotNull(recorded);
        CollectionAssert.AreEqual(
            new[] { "--cutover-operation", "operation-a" },
            recorded.ArgumentList.ToArray());
    }

    [TestMethod]
    public async Task AuthorizedDotNetStartRejectsInvalidOperationBeforeProcessCreation()
    {
        var started = false;
        var options = Options(
            new StubStoreHandoffInspector(),
            startProcess: _ =>
            {
                started = true;
                return Process.GetCurrentProcess();
            });
        using var operations = new BridgeHostCutoverProcessOperations(options);

        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            operations.StartDotNetActiveAuthorizedAsync(
                DotNetInstanceName,
                "invalid operation",
                default).AsTask());

        Assert.IsFalse(started);
    }

    [TestMethod]
    public async Task PersistentStartPropagatesBindingFailureAfterProcessCreation()
    {
        var options = Options(
            new StubStoreHandoffInspector(),
            startProcess: _ => Process.GetCurrentProcess());
        using var operations = new BridgeHostCutoverProcessOperations(options);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            operations.StartNodeActiveAndBindAsync(
                (_, _) => ValueTask.FromException(
                    new InvalidDataException("test checkpoint failure")),
                CancellationToken.None).AsTask());
    }

    [TestMethod]
    public async Task StartCallFailureMapsToOwnershipUncertain()
    {
        var options = Options(
            new StubStoreHandoffInspector(),
            startProcess: _ => throw new Win32Exception("test start failure"));
        using var operations = new BridgeHostCutoverProcessOperations(options);

        var error = await Assert.ThrowsExceptionAsync<BridgeHostCutoverOperationException>(() =>
            operations.StartDotNetActiveAsync(DotNetInstanceName, default).AsTask());

        Assert.AreEqual(BridgeCutoverFailureReason.OwnershipUncertain, error.Reason);
        Assert.IsInstanceOfType<Win32Exception>(error.InnerException);
    }

    private static BridgeHostCutoverProcessOperations Operations(
        QueueHttpMessageHandler handler,
        IBridgeStoreHandoffInspector? inspector = null,
        int maxProbeAttempts = 3) => new(
            Options(
                inspector ?? new StubStoreHandoffInspector(),
                maxProbeAttempts: maxProbeAttempts),
            handler);

    private static BridgeHostCutoverProcessOptions Options(
        IBridgeStoreHandoffInspector inspector,
        Uri? endpoint = null,
        Func<ProcessStartInfo, Process?>? startProcess = null,
        int maxProbeAttempts = 3) => new(
            endpoint ?? new Uri("http://127.0.0.1:18876/"),
            ControlToken,
            inspector,
            () => new ProcessStartInfo("node-fixture.exe"),
            _ => new ProcessStartInfo("dotnet-fixture.exe"),
            startProcess,
            maxProbeAttempts,
            TimeSpan.Zero);

    private static BridgeCutoverHostIdentity Node(int processId) => new(
        processId,
        "node",
        BridgeHostCutoverTransaction.CurrentManagementApiVersion,
        "active",
        ActiveOwner: true,
        InstanceName: "production");

    private static BridgeCutoverHostIdentity DotNet(int processId) => new(
        processId,
        "dotnet",
        BridgeHostCutoverTransaction.CurrentManagementApiVersion,
        "active",
        ActiveOwner: true,
        DotNetInstanceName);

    private sealed class StubStoreHandoffInspector(
        BridgeStoreHandoffEvidence? evidence = null) : IBridgeStoreHandoffInspector
    {
        private readonly BridgeStoreHandoffEvidence evidence = evidence ?? new(
            StoreFlushed: true,
            StoreCompatible: true,
            BridgeCutoverLeaseState.Missing);

        public int Calls { get; private set; }

        public ValueTask<BridgeStoreHandoffEvidence> InspectAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(evidence);
        }
    }

    private sealed record RecordedHttpRequest(
        HttpMethod Method,
        string Path,
        IReadOnlyDictionary<string, string> Headers,
        string? Body);

    private sealed class QueueHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>>
            responses = [];

        public List<RecordedHttpRequest> Requests { get; } = [];

        public void Enqueue(HttpStatusCode statusCode) =>
            responses.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)));

        public void EnqueueJson<T>(T value) =>
            responses.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(value),
                    Encoding.UTF8,
                    "application/json"),
            }));

        public void EnqueueIdentity(BridgeCutoverHostIdentity identity) => EnqueueJson(new
        {
            ok = true,
            processId = identity.ProcessId,
            hostKind = identity.HostKind,
            managementApiVersion = identity.ManagementApiVersion,
            ownershipMode = identity.OwnershipMode,
            activeOwner = identity.ActiveOwner,
            instanceName = identity.InstanceName,
        });

        public void EnqueueException(Exception error) =>
            responses.Enqueue((_, _) => Task.FromException<HttpResponseMessage>(error));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(
                pair => pair.Key,
                pair => string.Join(",", pair.Value),
                StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(
                request.Method,
                request.RequestUri!.AbsolutePath,
                headers,
                body));
            if (responses.Count == 0)
            {
                throw new InvalidOperationException("测试未配置 HTTP 响应。");
            }
            return await responses.Dequeue()(request, cancellationToken);
        }
    }
}
