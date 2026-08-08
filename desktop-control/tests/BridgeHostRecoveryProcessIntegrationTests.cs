using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using AiCliFeishu.Bridge.Adapters.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishuControl;

[TestClass]
[DoNotParallelize]
public sealed class BridgeHostRecoveryProcessIntegrationTests
{
    private const string ControlToken = "isolated-recovery-test-token";
    private const string NodeInstanceName = "production";
    private const string DotNetInstanceName = "production-dotnet";

    [TestMethod]
    public async Task InterruptedPersistentCutoverRecoversFromARealDotNetToARealNode()
    {
        await using var environment = await IsolatedRecoveryEnvironment.CreateAsync();
        var initialNodeProcessId = await environment.StartInitialFixtureAsync(
            "node",
            NodeInstanceName);
        var coordinator = environment.CreatePersistentCoordinator(
            async (writer, checkpoint, cancellationToken) =>
            {
                if (checkpoint.Stage is BridgeHostCutoverStage.Completed)
                {
                    return new(
                        BridgeHostCutoverCheckpointWriteState.Unavailable,
                        BridgeHostCutoverCheckpointReadState.Present);
                }
                return await writer.TryWriteAsync(checkpoint, cancellationToken);
            });

        var cutover = await coordinator.RunAsync(
            NodeIdentity(initialNodeProcessId),
            DotNetInstanceName);

        Assert.AreEqual(BridgeHostPersistentCutoverState.Unavailable, cutover.State);
        Assert.AreEqual(
            BridgeHostCutoverStage.DotNetActiveVerified,
            cutover.DurableSnapshot?.Stage);
        Assert.IsFalse(IsProcessAlive(initialNodeProcessId));
        Assert.AreEqual(0, environment.NodeStarts);
        Assert.AreEqual(1, environment.DotNetStarts);
        Assert.AreEqual(1, environment.StartedProcessIds.Count);
        var dotNetProcessId = environment.StartedProcessIds[0];
        Assert.IsTrue(IsProcessAlive(dotNetProcessId));
        Assert.AreEqual(
            BridgeHostCutoverStage.DotNetActiveVerified,
            (await environment.ReadCheckpointAsync()).Stage);
        await environment.AssertLiveLeaseAsync(
            "dotnet",
            DotNetInstanceName,
            dotNetProcessId);

        var recovery = await environment.Executor.RunAsync();

        Assert.AreEqual(BridgeHostRecoveryExecutionState.Recovered, recovery.State);
        Assert.AreEqual(
            BridgeHostRecoveryDisposition.RollBackDotNetToNode,
            recovery.Plan?.Disposition);
        Assert.IsFalse(IsProcessAlive(dotNetProcessId));
        Assert.AreEqual(1, environment.NodeStarts);
        Assert.AreEqual(1, environment.DotNetStarts);
        Assert.AreEqual(2, environment.StartedProcessIds.Count);
        var recoveredNodeProcessId = environment.StartedProcessIds[1];
        Assert.IsTrue(IsProcessAlive(recoveredNodeProcessId));

        var checkpoint = await environment.ReadCheckpointAsync();
        Assert.AreEqual(BridgeHostCutoverStage.RolledBack, checkpoint.Stage);
        Assert.AreEqual(recoveredNodeProcessId, checkpoint.NodeRollbackProcessId);
        Assert.AreEqual(
            BridgeCutoverFailureReason.OwnershipUncertain,
            checkpoint.FailureReason);
        await environment.AssertLiveLeaseAsync(
            "node",
            NodeInstanceName,
            recoveredNodeProcessId);
    }

    [TestMethod]
    public async Task CommittedPersistentCutoverRestartsARealDotNetWithoutRewritingCheckpoint()
    {
        await using var environment = await IsolatedRecoveryEnvironment.CreateAsync();
        var initialNodeProcessId = await environment.StartInitialFixtureAsync(
            "node",
            NodeInstanceName);

        var cutover = await environment.CreatePersistentCoordinator().RunAsync(
            NodeIdentity(initialNodeProcessId),
            DotNetInstanceName);

        Assert.AreEqual(BridgeHostPersistentCutoverState.Completed, cutover.State);
        Assert.AreEqual(BridgeHostCutoverStage.Completed, cutover.DurableSnapshot?.Stage);
        Assert.IsFalse(IsProcessAlive(initialNodeProcessId));
        Assert.AreEqual(0, environment.NodeStarts);
        Assert.AreEqual(1, environment.DotNetStarts);
        Assert.AreEqual(1, environment.StartedProcessIds.Count);
        var committedDotNetProcessId = environment.StartedProcessIds[0];
        Assert.IsTrue(IsProcessAlive(committedDotNetProcessId));

        var committedCheckpoint = await environment.ReadCheckpointAsync();
        Assert.AreEqual(BridgeHostCutoverStage.Completed, committedCheckpoint.Stage);
        Assert.AreEqual(committedDotNetProcessId, committedCheckpoint.DotNetProcessId);
        var committedBytes = await File.ReadAllBytesAsync(environment.CheckpointPath);
        await environment.AssertLiveLeaseAsync(
            "dotnet",
            DotNetInstanceName,
            committedDotNetProcessId);

        await environment.StopStartedFixtureAsync(
            committedDotNetProcessId,
            "dotnet");
        Assert.IsFalse(IsProcessAlive(committedDotNetProcessId));

        var recovery = await environment.Executor.RunAsync();

        Assert.AreEqual(BridgeHostRecoveryExecutionState.Recovered, recovery.State);
        Assert.AreEqual(
            BridgeHostRecoveryDisposition.RestartDotNet,
            recovery.Plan?.Disposition);
        Assert.AreEqual(0, environment.NodeStarts);
        Assert.AreEqual(2, environment.DotNetStarts);
        Assert.AreEqual(2, environment.StartedProcessIds.Count);
        var restartedDotNetProcessId = environment.StartedProcessIds[1];
        Assert.IsTrue(IsProcessAlive(restartedDotNetProcessId));
        CollectionAssert.AreEqual(
            committedBytes,
            await File.ReadAllBytesAsync(environment.CheckpointPath));
        Assert.AreEqual(
            BridgeHostCutoverStage.Completed,
            (await environment.ReadCheckpointAsync()).Stage);
        await environment.AssertLiveLeaseAsync(
            "dotnet",
            DotNetInstanceName,
            restartedDotNetProcessId);
    }

    [TestMethod]
    public async Task OfflinePreCommitCheckpointRestartsARealNodeAndConvergesRollback()
    {
        await using var environment = await IsolatedRecoveryEnvironment.CreateAsync();
        await environment.WriteCheckpointAsync(BridgeHostCutoverStage.Planned);

        var result = await environment.Executor.RunAsync();

        Assert.AreEqual(BridgeHostRecoveryExecutionState.Recovered, result.State);
        Assert.AreEqual(BridgeHostRecoveryDisposition.RestartNode, result.Plan?.Disposition);
        Assert.AreEqual(1, environment.NodeStarts);
        Assert.AreEqual(0, environment.DotNetStarts);
        Assert.AreEqual(1, environment.StartedProcessIds.Count);
        Assert.IsTrue(IsProcessAlive(environment.StartedProcessIds[0]));

        var checkpoint = await environment.ReadCheckpointAsync();
        Assert.AreEqual(BridgeHostCutoverStage.RolledBack, checkpoint.Stage);
        Assert.AreEqual(environment.StartedProcessIds[0], checkpoint.NodeRollbackProcessId);
        Assert.AreEqual(BridgeCutoverFailureReason.OwnershipUncertain, checkpoint.FailureReason);
        await environment.AssertLiveLeaseAsync(
            "node",
            NodeInstanceName,
            environment.StartedProcessIds[0]);
    }

    [TestMethod]
    public async Task OfflineCommittedCheckpointRestartsARealDotNetAndPreservesCheckpointBytes()
    {
        await using var environment = await IsolatedRecoveryEnvironment.CreateAsync();
        await environment.WriteCheckpointAsync(
            BridgeHostCutoverStage.Completed,
            dotNetProcessId: 73001);
        var before = await File.ReadAllBytesAsync(environment.CheckpointPath);

        var result = await environment.Executor.RunAsync();

        Assert.AreEqual(BridgeHostRecoveryExecutionState.Recovered, result.State);
        Assert.AreEqual(BridgeHostRecoveryDisposition.RestartDotNet, result.Plan?.Disposition);
        Assert.AreEqual(0, environment.NodeStarts);
        Assert.AreEqual(1, environment.DotNetStarts);
        Assert.AreEqual(1, environment.StartedProcessIds.Count);
        Assert.IsTrue(IsProcessAlive(environment.StartedProcessIds[0]));
        CollectionAssert.AreEqual(
            before,
            await File.ReadAllBytesAsync(environment.CheckpointPath));
        Assert.AreEqual(
            BridgeHostCutoverStage.Completed,
            (await environment.ReadCheckpointAsync()).Stage);
        await environment.AssertLiveLeaseAsync(
            "dotnet",
            DotNetInstanceName,
            environment.StartedProcessIds[0]);
    }

    [TestMethod]
    public async Task LivePreCommitDotNetIsStoppedBeforeARealNodeRollbackStarts()
    {
        await using var environment = await IsolatedRecoveryEnvironment.CreateAsync();
        var dotNetProcessId = await environment.StartInitialFixtureAsync(
            "dotnet",
            DotNetInstanceName);
        await environment.WriteCheckpointAsync(
            BridgeHostCutoverStage.DotNetStartRequested,
            dotNetProcessId);

        var result = await environment.Executor.RunAsync();

        Assert.AreEqual(BridgeHostRecoveryExecutionState.Recovered, result.State);
        Assert.AreEqual(
            BridgeHostRecoveryDisposition.RollBackDotNetToNode,
            result.Plan?.Disposition);
        Assert.IsFalse(IsProcessAlive(dotNetProcessId));
        Assert.AreEqual(1, environment.NodeStarts);
        Assert.AreEqual(0, environment.DotNetStarts);
        Assert.AreEqual(1, environment.StartedProcessIds.Count);
        Assert.IsTrue(IsProcessAlive(environment.StartedProcessIds[0]));

        var checkpoint = await environment.ReadCheckpointAsync();
        Assert.AreEqual(BridgeHostCutoverStage.RolledBack, checkpoint.Stage);
        Assert.AreEqual(environment.StartedProcessIds[0], checkpoint.NodeRollbackProcessId);
        await environment.AssertLiveLeaseAsync(
            "node",
            NodeInstanceName,
            environment.StartedProcessIds[0]);
    }

    [TestMethod]
    public async Task MismatchedLiveDotNetIdentityIsNeverStoppedOrReplaced()
    {
        await using var environment = await IsolatedRecoveryEnvironment.CreateAsync();
        var dotNetProcessId = await environment.StartInitialFixtureAsync(
            "dotnet",
            "different-dotnet-instance");
        await environment.WriteCheckpointAsync(
            BridgeHostCutoverStage.DotNetStartRequested,
            dotNetProcessId);

        var result = await environment.Executor.RunAsync();

        Assert.AreEqual(BridgeHostRecoveryExecutionState.ManualIntervention, result.State);
        Assert.AreEqual(
            BridgeHostRecoveryReason.UnexpectedEndpointIdentity,
            result.Plan?.Reason);
        Assert.IsTrue(IsProcessAlive(dotNetProcessId));
        Assert.AreEqual(0, environment.NodeStarts);
        Assert.AreEqual(0, environment.DotNetStarts);
        Assert.AreEqual(0, environment.StartedProcessIds.Count);
        Assert.AreEqual(
            BridgeHostCutoverStage.DotNetStartRequested,
            (await environment.ReadCheckpointAsync()).Stage);
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private sealed class IsolatedRecoveryEnvironment : IAsyncDisposable
    {
        private readonly string root;
        private readonly string dataDirectory;
        private readonly int port;
        private readonly Uri endpoint;
        private readonly BridgeHostCutoverProcessOperations operations;
        private readonly BridgeHostRecoveryObserver observer;
        private readonly List<TrackedProcess> initialProcesses = [];

        private IsolatedRecoveryEnvironment(
            string root,
            string dataDirectory,
            int port,
            Uri endpoint,
            BridgeHostCutoverProcessOperations operations,
            BridgeHostRecoveryObserver observer)
        {
            this.root = root;
            this.dataDirectory = dataDirectory;
            this.port = port;
            this.endpoint = endpoint;
            this.operations = operations;
            this.observer = observer;
            Executor = new(dataDirectory, observer, operations);
        }

        public BridgeHostRecoveryExecutor Executor { get; }

        public List<int> StartedProcessIds { get; } = [];

        public int NodeStarts { get; private set; }

        public int DotNetStarts { get; private set; }

        public string CheckpointPath =>
            Path.Combine(dataDirectory, BridgeHostCutoverCheckpointStore.CheckpointFileName);

        public BridgeHostPersistentCutoverCoordinator CreatePersistentCoordinator() =>
            new(dataDirectory, operations);

        public BridgeHostPersistentCutoverCoordinator CreatePersistentCoordinator(
            BridgeHostPersistentCutoverCoordinator.WriteCheckpointAsync writeCheckpoint) =>
            new(
                dataDirectory,
                operations,
                TimeProvider.System,
                static () => "integration-persistent-cutover",
                writeCheckpoint);

        public Task StopStartedFixtureAsync(int processId, string hostKind) =>
            StopFixtureAsync(processId, endpoint, hostKind);

        public static Task<IsolatedRecoveryEnvironment> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"ai-cli-feishu-recovery-{Guid.NewGuid():N}");
            var dataDirectory = Path.Combine(root, "isolated-data");
            Directory.CreateDirectory(dataDirectory);
            var port = FindFreeLoopbackPort();
            var endpoint = new Uri(
                $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/");

            IsolatedRecoveryEnvironment? environment = null;
            var options = new BridgeHostCutoverProcessOptions(
                endpoint,
                ControlToken,
                new LeaseBackedStoreHandoffInspector(dataDirectory),
                () =>
                {
                    environment!.NodeStarts++;
                    return CreateFixtureStartInfo(
                        root,
                        dataDirectory,
                        port,
                        "node",
                        NodeInstanceName);
                },
                instanceName =>
                {
                    environment!.DotNetStarts++;
                    return CreateFixtureStartInfo(
                        root,
                        dataDirectory,
                        port,
                        "dotnet",
                        instanceName);
                },
                startInfo =>
                {
                    var process = Process.Start(startInfo);
                    if (process is not null)
                    {
                        environment!.StartedProcessIds.Add(process.Id);
                    }
                    return process;
                },
                MaxProbeAttempts: 161,
                PollInterval: TimeSpan.FromMilliseconds(25));
            var operations = new BridgeHostCutoverProcessOperations(options);
            var observer = new BridgeHostRecoveryObserver(
                dataDirectory,
                endpoint,
                ControlToken);
            environment = new(
                root,
                dataDirectory,
                port,
                endpoint,
                operations,
                observer);
            return Task.FromResult(environment);
        }

        public async Task<int> StartInitialFixtureAsync(
            string hostKind,
            string instanceName)
        {
            var process = StartFixture(
                root,
                dataDirectory,
                port,
                hostKind,
                instanceName);
            initialProcesses.Add(new(process.Id, hostKind));
            try
            {
                await WaitForReadyAsync(endpoint, process);
                return process.Id;
            }
            finally
            {
                process.Dispose();
            }
        }

        public async Task WriteCheckpointAsync(
            BridgeHostCutoverStage stage,
            int dotNetProcessId = 0)
        {
            var checkpoint = new BridgeHostCutoverCheckpoint(
                BridgeHostCutoverCheckpoint.CurrentSchemaVersion,
                "integration-recovery",
                DateTimeOffset.Parse("2026-08-07T12:00:00.000Z"),
                stage,
                RequiresRollback: false,
                BridgeCutoverFailureReason.None,
                new(
                    ProcessId: 72001,
                    HostKind: "node",
                    BridgeHostCutoverTransaction.CurrentManagementApiVersion,
                    OwnershipMode: "active",
                    ActiveOwner: true,
                    NodeInstanceName),
                DotNetInstanceName,
                dotNetProcessId,
                NodeRollbackProcessId: 0);
            await new BridgeHostCutoverCheckpointStore(dataDirectory)
                .WriteAsync(checkpoint);
        }

        public async Task<BridgeHostCutoverCheckpoint> ReadCheckpointAsync()
        {
            var result = await new BridgeHostCutoverCheckpointStore(dataDirectory)
                .ReadAsync();
            Assert.AreEqual(BridgeHostCutoverCheckpointReadState.Present, result.State);
            return result.Checkpoint ??
                throw new AssertFailedException("Recovery checkpoint was not present.");
        }

        public async Task AssertLiveLeaseAsync(
            string hostKind,
            string instanceName,
            int processId)
        {
            var lease = await new ActiveOwnerLeaseObserver(dataDirectory).InspectAsync();
            Assert.AreEqual(ActiveOwnerLeaseState.Live, lease.State);
            Assert.IsNotNull(lease.Record);
            Assert.AreEqual(hostKind, lease.Record.HostKind);
            Assert.AreEqual(instanceName, lease.Record.InstanceName);
            Assert.AreEqual(processId, lease.Record.ProcessId);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var processId in StartedProcessIds.AsEnumerable().Reverse())
            {
                await StopFixtureAsync(processId, endpoint, HostKindFor(processId));
            }
            foreach (var process in initialProcesses.AsEnumerable().Reverse())
            {
                await StopFixtureAsync(process.ProcessId, endpoint, process.HostKind);
            }
            observer.Dispose();
            operations.Dispose();
            await DeleteDirectoryWithRetryAsync(root);
        }

        private string HostKindFor(int processId)
        {
            var index = StartedProcessIds.IndexOf(processId);
            if (NodeStarts > 0 && index == StartedProcessIds.Count - 1)
            {
                return "node";
            }
            return "dotnet";
        }
    }

    private sealed record TrackedProcess(int ProcessId, string HostKind);

    private static BridgeCutoverHostIdentity NodeIdentity(int processId) =>
        new(
            processId,
            "node",
            BridgeHostCutoverTransaction.CurrentManagementApiVersion,
            "active",
            ActiveOwner: true,
            NodeInstanceName);

    private sealed class LeaseBackedStoreHandoffInspector(string dataDirectory) :
        IBridgeStoreHandoffInspector
    {
        private readonly ActiveOwnerLeaseObserver leaseObserver = new(dataDirectory);

        public async ValueTask<BridgeStoreHandoffEvidence> InspectAsync(
            CancellationToken cancellationToken)
        {
            var snapshot = await leaseObserver.InspectAsync(cancellationToken);
            var leaseState = snapshot.State switch
            {
                ActiveOwnerLeaseState.Missing => BridgeCutoverLeaseState.Missing,
                ActiveOwnerLeaseState.Stale => BridgeCutoverLeaseState.Stale,
                ActiveOwnerLeaseState.Live => BridgeCutoverLeaseState.Live,
                _ => BridgeCutoverLeaseState.Invalid,
            };
            return new(
                StoreFlushed: true,
                StoreCompatible: true,
                leaseState);
        }
    }

    private static Process StartFixture(
        string workingDirectory,
        string dataDirectory,
        int port,
        string hostKind,
        string instanceName) =>
        Process.Start(CreateFixtureStartInfo(
            workingDirectory,
            dataDirectory,
            port,
            hostKind,
            instanceName)) ?? throw new InvalidOperationException("Fixture process did not start.");

    private static ProcessStartInfo CreateFixtureStartInfo(
        string workingDirectory,
        string dataDirectory,
        int port,
        string hostKind,
        string instanceName)
    {
        var executable = Path.Combine(
            AppContext.BaseDirectory,
            "cutover-host-fixture",
            OperatingSystem.IsWindows()
                ? "CutoverHostFixture.exe"
                : "CutoverHostFixture");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                "Cutover Host fixture was not copied to test output.",
                executable);
        }
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("--host-kind");
        startInfo.ArgumentList.Add(hostKind);
        startInfo.ArgumentList.Add("--instance");
        startInfo.ArgumentList.Add(instanceName);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--token");
        startInfo.ArgumentList.Add(ControlToken);
        startInfo.ArgumentList.Add("--data-directory");
        startInfo.ArgumentList.Add(dataDirectory);
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        return startInfo;
    }

    private static async Task WaitForReadyAsync(Uri endpoint, Process process)
    {
        using var client = new HttpClient { BaseAddress = endpoint };
        for (var attempt = 1; attempt <= 100; attempt++)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Recovery Host fixture exited with code {process.ExitCode}.");
            }
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "health");
                request.Headers.Add("X-AI-CLI-Feishu-Control-Token", ControlToken);
                using var response = await client.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
        throw new TimeoutException("Recovery Host fixture did not become ready.");
    }

    private static async Task StopFixtureAsync(
        int processId,
        Uri endpoint,
        string hostKind)
    {
        if (!IsProcessAlive(processId))
        {
            return;
        }
        try
        {
            using var client = new HttpClient { BaseAddress = endpoint };
            using var request = new HttpRequestMessage(HttpMethod.Post, "control/shutdown")
            {
                Content = new StringContent(
                    "{}",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.Add("X-AI-CLI-Feishu-Control-Token", ControlToken);
            request.Headers.Add("X-AI-CLI-Feishu-Expected-Host-Kind", hostKind);
            request.Headers.Add("X-AI-CLI-Feishu-Management-Api-Version", "1");
            request.Headers.Add(
                "X-AI-CLI-Feishu-Expected-Process-Id",
                processId.ToString(CultureInfo.InvariantCulture));
            using var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                for (var attempt = 1; attempt <= 80 && IsProcessAlive(processId); attempt++)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(25));
                }
            }
        }
        catch (HttpRequestException)
        {
        }

        if (!IsProcessAlive(processId))
        {
            return;
        }
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        catch (ArgumentException)
        {
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path)
    {
        for (var attempt = 1; attempt <= 40; attempt++)
        {
            if (!Directory.Exists(path))
            {
                return;
            }
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 40)
            {
            }
            catch (UnauthorizedAccessException) when (attempt < 40)
            {
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        Directory.Delete(path, recursive: true);
    }

    private static int FindFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
