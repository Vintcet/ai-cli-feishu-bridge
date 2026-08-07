using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishuControl;

[TestClass]
public sealed class BridgeHostRecoveryObserverTests
{
    private const string NodeInstanceName = "production";
    private const string DotNetInstanceName = "production-dotnet";
    private string? directory;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            $"ai-cli-feishu-recovery-observer-{Guid.NewGuid():N}");
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
    public async Task StableAuthenticatedNodeProducesANoOpPlan()
    {
        var checkpoint = Checkpoint(BridgeHostCutoverStage.Planned);
        var identity = Node(83001);
        var calls = new List<string>();
        using var observer = Observer(
            [Present(checkpoint), Present(checkpoint)],
            [Authenticated(identity), Authenticated(identity)],
            [LiveLease(identity, "node-live"), LiveLease(identity, "node-live")],
            calls);

        var result = await observer.InspectAsync();

        Assert.AreEqual(BridgeHostCutoverCheckpointReadState.Present, result.CheckpointState);
        Assert.AreEqual(
            BridgeHostRecoveryDisposition.NodeAlreadyActive,
            result.Plan.Disposition);
        Assert.AreEqual(BridgeHostRecoveryReason.None, result.Plan.Reason);
        CollectionAssert.AreEqual(
            new[] { "checkpoint", "endpoint", "lease", "endpoint", "lease", "checkpoint" },
            calls.ToArray());
    }

    [TestMethod]
    public async Task StableOfflineMissingOwnerUsesTheCommitPointToChooseTheRuntime()
    {
        var checkpoint = Checkpoint(BridgeHostCutoverStage.Completed);
        using var observer = Observer(
            [Present(checkpoint), Present(checkpoint)],
            [BridgeHostRecoveryEndpointObservation.Offline(),
                BridgeHostRecoveryEndpointObservation.Offline()],
            [MissingLease(), MissingLease()]);

        var result = await observer.InspectAsync();

        Assert.AreEqual(BridgeHostRecoveryDisposition.RestartDotNet, result.Plan.Disposition);
        CollectionAssert.AreEqual(
            new[]
            {
                BridgeHostRecoveryStep.InspectStoreHandoff,
                BridgeHostRecoveryStep.StartDotNet,
                BridgeHostRecoveryStep.VerifyDotNetActive,
            },
            result.Plan.Steps.ToArray());
    }

    [TestMethod]
    public async Task MissingInvalidAndUnavailableCheckpointsNeverProbeTheEndpoint()
    {
        foreach (var (state, reason) in new[]
        {
            (BridgeHostCutoverCheckpointReadState.Missing,
                BridgeHostRecoveryReason.CheckpointMissing),
            (BridgeHostCutoverCheckpointReadState.Invalid,
                BridgeHostRecoveryReason.InvalidCheckpoint),
            (BridgeHostCutoverCheckpointReadState.Unavailable,
                BridgeHostRecoveryReason.CheckpointUnavailable),
        })
        {
            var endpointCalls = 0;
            var leaseCalls = 0;
            using var observer = new BridgeHostRecoveryObserver(
                _ =>
                {
                    return ValueTask.FromResult(
                        new BridgeHostCutoverCheckpointReadResult(state));
                },
                _ =>
                {
                    endpointCalls++;
                    throw new InvalidOperationException("endpoint must not be probed");
                },
                _ =>
                {
                    leaseCalls++;
                    throw new InvalidOperationException("lease must not be probed");
                });

            var result = await observer.InspectAsync();

            Assert.AreEqual(state, result.CheckpointState, state.ToString());
            Assert.AreEqual(reason, result.Plan.Reason, state.ToString());
            Assert.AreEqual(
                BridgeHostRecoveryDisposition.ManualIntervention,
                result.Plan.Disposition,
                state.ToString());
            Assert.AreEqual(0, endpointCalls, state.ToString());
            Assert.AreEqual(0, leaseCalls, state.ToString());
        }
    }

    [TestMethod]
    public async Task ACheckpointChangedDuringObservationRequiresManualIntervention()
    {
        var before = Checkpoint(BridgeHostCutoverStage.Planned, "operation-a");
        var after = Checkpoint(BridgeHostCutoverStage.Completed, "operation-b", 83002);
        var identity = Node(83003);
        using var observer = Observer(
            [Present(before), Present(after)],
            [Authenticated(identity), Authenticated(identity)],
            [LiveLease(identity, "node-live"), LiveLease(identity, "node-live")]);

        var result = await observer.InspectAsync();

        Assert.AreEqual(BridgeHostRecoveryReason.CheckpointChanged, result.Plan.Reason);
        Assert.AreEqual(
            BridgeHostRecoveryDisposition.ManualIntervention,
            result.Plan.Disposition);
    }

    [TestMethod]
    public async Task ARawRewriteWithTheSameCheckpointIsStillAChangedObservation()
    {
        var checkpoint = Checkpoint(BridgeHostCutoverStage.Planned);
        var identity = Node(83012);
        using var observer = Observer(
            [
                new(
                    BridgeHostCutoverCheckpointReadState.Present,
                    checkpoint,
                    "version-a"),
                new(
                    BridgeHostCutoverCheckpointReadState.Present,
                    checkpoint,
                    "version-b"),
            ],
            [Authenticated(identity), Authenticated(identity)],
            [LiveLease(identity, "node-live"), LiveLease(identity, "node-live")]);

        var result = await observer.InspectAsync();

        Assert.AreEqual(BridgeHostRecoveryReason.CheckpointChanged, result.Plan.Reason);
    }

    [TestMethod]
    public async Task EndpointChangesDuringObservationCannotProduceAPlan()
    {
        var checkpoint = Checkpoint(BridgeHostCutoverStage.Planned);
        var node = Node(83004);
        var dotNet = DotNet(83005);
        using var observer = Observer(
            [Present(checkpoint), Present(checkpoint)],
            [Authenticated(node), Authenticated(dotNet)],
            [LiveLease(node, "node-live"), LiveLease(node, "node-live")]);

        var result = await observer.InspectAsync();

        Assert.AreEqual(BridgeHostRecoveryReason.ObservationChanged, result.Plan.Reason);
        Assert.AreEqual(
            BridgeHostRecoveryDisposition.ManualIntervention,
            result.Plan.Disposition);
    }

    [TestMethod]
    public async Task LeaseIdentityChangesDuringObservationCannotProduceAPlan()
    {
        var checkpoint = Checkpoint(BridgeHostCutoverStage.Planned);
        var node = Node(83006);
        using var observer = Observer(
            [Present(checkpoint), Present(checkpoint)],
            [Authenticated(node), Authenticated(node)],
            [LiveLease(node, "lease-a"), LiveLease(node, "lease-b")]);

        var result = await observer.InspectAsync();

        Assert.AreEqual(BridgeHostRecoveryReason.ObservationChanged, result.Plan.Reason);
    }

    [TestMethod]
    public async Task StableUncertainEndpointIsNeverTreatedAsOffline()
    {
        var checkpoint = Checkpoint(BridgeHostCutoverStage.Completed);
        using var observer = Observer(
            [Present(checkpoint), Present(checkpoint)],
            [BridgeHostRecoveryEndpointObservation.Uncertain(),
                BridgeHostRecoveryEndpointObservation.Uncertain()],
            [MissingLease(), MissingLease()]);

        var result = await observer.InspectAsync();

        Assert.AreEqual(BridgeHostRecoveryReason.EndpointUncertain, result.Plan.Reason);
        Assert.AreEqual(
            BridgeHostRecoveryDisposition.ManualIntervention,
            result.Plan.Disposition);
    }

    [TestMethod]
    public async Task ForgedEndpointOrLeaseEvidenceIsDowngradedToManual()
    {
        var checkpoint = Checkpoint(BridgeHostCutoverStage.Planned);
        using (var invalidEndpoint = Observer(
                   [Present(checkpoint), Present(checkpoint)],
                   [new(
                       BridgeHostRecoveryEndpointState.Authenticated,
                       null),
                       new(
                           BridgeHostRecoveryEndpointState.Authenticated,
                           null)],
                   [MissingLease(), MissingLease()]))
        {
            var result = await invalidEndpoint.InspectAsync();
            Assert.AreEqual(
                BridgeHostRecoveryReason.EndpointUncertain,
                result.Plan.Reason);
        }

        using (var invalidLease = Observer(
                   [Present(checkpoint), Present(checkpoint)],
                   [BridgeHostRecoveryEndpointObservation.Offline(),
                       BridgeHostRecoveryEndpointObservation.Offline()],
                   [new(ActiveOwnerLeaseState.Live), new(ActiveOwnerLeaseState.Live)]))
        {
            var result = await invalidLease.InspectAsync();
            Assert.AreEqual(
                BridgeHostRecoveryReason.ActiveOwnerLeaseInvalid,
                result.Plan.Reason);
        }
    }

    [TestMethod]
    public async Task CancellationStopsTheReadOnlySequence()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = new List<string>();
        using var observer = new BridgeHostRecoveryObserver(
            token =>
            {
                calls.Add("checkpoint");
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult(
                    new BridgeHostCutoverCheckpointReadResult(
                        BridgeHostCutoverCheckpointReadState.Missing));
            },
            _ => throw new InvalidOperationException("endpoint must not be reached"),
            _ => throw new InvalidOperationException("lease must not be reached"));

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
            await observer.InspectAsync(cancellation.Token));
        CollectionAssert.AreEqual(new[] { "checkpoint" }, calls.ToArray());
    }

    [TestMethod]
    public async Task ProductionObserverOnlyReadsCheckpointLeaseAndEndpoint()
    {
        Directory.CreateDirectory(directory!);
        var checkpoint = Checkpoint(
            BridgeHostCutoverStage.Planned,
            operationId: "production-read-only",
            nodeProcessId: Environment.ProcessId);
        var checkpointStore = new BridgeHostCutoverCheckpointStore(directory!);
        await checkpointStore.WriteAsync(checkpoint);

        var lockDirectory = Path.Combine(
            directory!,
            ActiveOwnerLeaseObserver.LockDirectoryName);
        Directory.CreateDirectory(lockDirectory);
        var leasePath = Path.Combine(
            lockDirectory,
            ActiveOwnerLeaseObserver.MetadataFileName);
        var lease = new ActiveOwnerLeaseRecord(
            ActiveOwnerLeaseObserver.SchemaVersion,
            "node",
            "active",
            Environment.ProcessId,
            NodeInstanceName,
            "production-read-only-lease",
            DateTimeOffset.Parse("2026-08-07T12:00:00.000Z"));
        await File.WriteAllTextAsync(
            leasePath,
            JsonSerializer.Serialize(
                lease,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                }));
        var before = SnapshotFiles(directory!);
        using var observer = new BridgeHostRecoveryObserver(
            directory!,
            new Uri("http://127.0.0.1:8876/"),
            "test-control-token",
            new IdentityHandler(Node(Environment.ProcessId)));

        var result = await observer.InspectAsync();

        Assert.AreEqual(
            BridgeHostRecoveryDisposition.NodeAlreadyActive,
            result.Plan.Disposition);
        CollectionAssert.AreEqual(before, SnapshotFiles(directory!));
    }

    [TestMethod]
    public async Task EndpointProbeAcceptsAnAuthenticatedIdentityEvenWhenHealthIsFaulted()
    {
        using var probe = new BridgeHostRecoveryEndpointProbe(
            new Uri("http://127.0.0.1:8876/"),
            "test-control-token",
            new IdentityHandler(Node(83010) with { ActiveOwner = false }));

        var observation = await probe.InspectAsync();

        Assert.AreEqual(
            BridgeHostRecoveryEndpointState.Authenticated,
            observation.State);
        Assert.AreEqual(Node(83010) with { ActiveOwner = false }, observation.Identity);
    }

    [DataTestMethod]
    [DataRow("{\"ok\":true}")]
    [DataRow("{\"hostKind\":\"node\",\"processId\":0}")]
    [DataRow("not-json")]
    public async Task EndpointProbeNeverTreatsAnUnauthenticatedOrMalformedResponseAsOffline(
        string body)
    {
        using var probe = new BridgeHostRecoveryEndpointProbe(
            new Uri("http://127.0.0.1:8876/"),
            "test-control-token",
            new BodyHandler(body));

        var observation = await probe.InspectAsync();

        Assert.AreEqual(BridgeHostRecoveryEndpointState.Uncertain, observation.State);
    }

    [TestMethod]
    public async Task ConnectionRefusedIsTheOnlyTransportFailureClassifiedAsOffline()
    {
        using var probe = new BridgeHostRecoveryEndpointProbe(
            new Uri("http://127.0.0.1:8876/"),
            "test-control-token",
            new ThrowingHandler(
                new HttpRequestException(
                    "connection refused",
                    new SocketException((int)SocketError.ConnectionRefused))));

        var observation = await probe.InspectAsync();

        Assert.AreEqual(BridgeHostRecoveryEndpointState.Offline, observation.State);
    }

    [TestMethod]
    public async Task EndpointProbePropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var probe = new BridgeHostRecoveryEndpointProbe(
            new Uri("http://127.0.0.1:8876/"),
            "test-control-token",
            new ThrowingHandler(new TaskCanceledException("timed out")));
        cancellation.Cancel();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () =>
            await probe.InspectAsync(cancellation.Token));
    }

    [TestMethod]
    public async Task AutomaticInspectionCarriesOnlyTheStableCheckpointVersion()
    {
        var checkpoint = Checkpoint(BridgeHostCutoverStage.Planned);
        var endpoint = BridgeHostRecoveryEndpointObservation.Offline();
        var lease = MissingLease();
        using var observer = Observer(
            [
                new(
                    BridgeHostCutoverCheckpointReadState.Present,
                    checkpoint,
                    "version-a"),
                new(
                    BridgeHostCutoverCheckpointReadState.Present,
                    checkpoint,
                    "version-a"),
            ],
            [endpoint, endpoint],
            [lease, lease]);

        var result = await observer.InspectAsync();

        Assert.AreEqual("version-a", result.CheckpointFileVersion);
        CollectionAssert.AreEquivalent(
            new[] { "CheckpointFileVersion", "CheckpointState", "Plan" },
            typeof(BridgeHostRecoveryInspection)
                .GetProperties()
                .Select(property => property.Name)
                .ToArray());
    }
    private BridgeHostRecoveryObserver Observer(
        IEnumerable<BridgeHostCutoverCheckpointReadResult> checkpoints,
        IEnumerable<BridgeHostRecoveryEndpointObservation> endpoints,
        IEnumerable<ActiveOwnerLeaseSnapshot> leases,
        List<string>? calls = null) =>
        new(
            Sequence(checkpoints, "checkpoint", calls),
            Sequence(endpoints, "endpoint", calls),
            Sequence(leases, "lease", calls));

    private static Func<CancellationToken, ValueTask<T>> Sequence<T>(
        IEnumerable<T> values,
        string name,
        List<string>? calls)
    {
        var remaining = new Queue<T>(values);
        return cancellationToken =>
        {
            calls?.Add(name);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(remaining.Dequeue());
        };
    }

    private static BridgeHostCutoverCheckpointReadResult Present(
        BridgeHostCutoverCheckpoint checkpoint) =>
        new(BridgeHostCutoverCheckpointReadState.Present, checkpoint);

    private static BridgeHostCutoverCheckpoint Checkpoint(
        BridgeHostCutoverStage stage,
        string operationId = "recovery-operation",
        int nodeProcessId = 83000,
        int dotNetProcessId = 0) =>
        new BridgeHostCutoverCheckpoint(
            BridgeHostCutoverCheckpoint.CurrentSchemaVersion,
            operationId,
            DateTimeOffset.Parse("2026-08-07T12:00:00.000Z"),
            stage,
            RequiresRollback(stage),
            FailureReason(stage),
            Node(nodeProcessId),
            DotNetInstanceName,
            dotNetProcessId is 0 &&
                stage is BridgeHostCutoverStage.DotNetStartRequested or
                    BridgeHostCutoverStage.DotNetActiveVerified or
                    BridgeHostCutoverStage.Completed
                ? 83099
                : dotNetProcessId,
            0).Validate();

    private static bool RequiresRollback(BridgeHostCutoverStage stage) =>
        stage is BridgeHostCutoverStage.RollbackRequired or
            BridgeHostCutoverStage.DotNetStopRequested or
            BridgeHostCutoverStage.DotNetOfflineVerified or
            BridgeHostCutoverStage.NodeRollbackStartRequested or
            BridgeHostCutoverStage.FailedSafe;

    private static BridgeCutoverFailureReason FailureReason(
        BridgeHostCutoverStage stage) =>
        stage is BridgeHostCutoverStage.Planned or
            BridgeHostCutoverStage.NodeStopRequested or
            BridgeHostCutoverStage.NodeOfflineVerified or
            BridgeHostCutoverStage.StoreHandoffVerified or
            BridgeHostCutoverStage.DotNetStartRequested or
            BridgeHostCutoverStage.DotNetActiveVerified or
            BridgeHostCutoverStage.Completed
            ? BridgeCutoverFailureReason.None
            : BridgeCutoverFailureReason.OwnershipUncertain;

    private static BridgeHostRecoveryEndpointObservation Authenticated(
        BridgeCutoverHostIdentity identity) =>
        BridgeHostRecoveryEndpointObservation.Authenticated(identity);

    private static ActiveOwnerLeaseSnapshot MissingLease() =>
        new(ActiveOwnerLeaseState.Missing);

    private static ActiveOwnerLeaseSnapshot LiveLease(
        BridgeCutoverHostIdentity identity,
        string leaseId) =>
        new(
            ActiveOwnerLeaseState.Live,
            new ActiveOwnerLeaseRecord(
                ActiveOwnerLeaseObserver.SchemaVersion,
                identity.HostKind,
                identity.OwnershipMode,
                identity.ProcessId,
                identity.InstanceName,
                leaseId,
                DateTimeOffset.Parse("2026-08-07T12:00:00.000Z")));

    private static BridgeCutoverHostIdentity Node(int processId) =>
        new(
            processId,
            "node",
            BridgeHostCutoverTransaction.CurrentManagementApiVersion,
            "active",
            true,
            NodeInstanceName);

    private static BridgeCutoverHostIdentity DotNet(int processId) =>
        new(
            processId,
            "dotnet",
            BridgeHostCutoverTransaction.CurrentManagementApiVersion,
            "active",
            true,
            DotNetInstanceName);

    private static string[] SnapshotFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path =>
                $"{Path.GetRelativePath(root, path)}:" +
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private sealed class IdentityHandler(BridgeCutoverHostIdentity identity) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.IsTrue(request.Headers.TryGetValues(
                "X-AI-CLI-Feishu-Control-Token",
                out var values));
            Assert.AreEqual("test-control-token", values.Single());
            var body = JsonSerializer.Serialize(new
            {
                ok = false,
                identity.HostKind,
                managementApiVersion = identity.ManagementApiVersion,
                identity.InstanceName,
                identity.ProcessId,
                identity.OwnershipMode,
                identity.ActiveOwner,
            }, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class BodyHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class ThrowingHandler(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(error);
    }
}
