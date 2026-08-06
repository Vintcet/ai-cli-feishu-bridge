using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishuControl;

[TestClass]
[DoNotParallelize]
public sealed class BridgeHostCutoverProcessIntegrationTests
{
    private const string ControlToken = "isolated-cutover-test-token";
    private const string NodeInstanceName = "production";
    private const string DotNetInstanceName = "production-dotnet";

    [TestMethod]
    public async Task RealProcessesCompleteAnIsolatedNodeToDotNetCutover()
    {
        await using var environment = await IsolatedCutoverEnvironment.StartAsync(
            BridgeCutoverLeaseState.Missing);

        var result = await environment.Coordinator.RunAsync(
            environment.InitialNodeIdentity,
            DotNetInstanceName);

        Assert.AreEqual(BridgeHostCutoverStage.Completed, result.Snapshot.Stage);
        Assert.AreEqual(1, environment.DotNetStarts);
        Assert.AreEqual(0, environment.NodeRollbackStarts);
        Assert.IsFalse(IsProcessAlive(environment.InitialNodeIdentity.ProcessId));
        Assert.IsTrue(environment.StartedProcessIds.Any(IsProcessAlive));
    }

    [TestMethod]
    public async Task WrongDotNetIdentityIsStoppedBeforeARealNodeRollbackStarts()
    {
        await using var environment = await IsolatedCutoverEnvironment.StartAsync(
            BridgeCutoverLeaseState.Stale,
            dotNetInstanceName: "wrong-dotnet-instance");

        var result = await environment.Coordinator.RunAsync(
            environment.InitialNodeIdentity,
            DotNetInstanceName);

        Assert.AreEqual(BridgeHostCutoverStage.RolledBack, result.Snapshot.Stage);
        Assert.AreEqual(
            BridgeCutoverFailureReason.DotNetIdentityMismatch,
            result.Snapshot.FailureReason);
        Assert.AreEqual(1, environment.DotNetStarts);
        Assert.AreEqual(1, environment.NodeRollbackStarts);
        Assert.AreEqual(2, environment.StartedProcessIds.Count);
        Assert.IsFalse(IsProcessAlive(environment.StartedProcessIds[0]));
        Assert.IsTrue(IsProcessAlive(environment.StartedProcessIds[1]));
    }

    [DataTestMethod]
    [DataRow("Live", "ActiveOwnerLive")]
    [DataRow("Invalid", "ActiveOwnerInvalid")]
    public async Task LeaseConflictNeverStartsASecondRealOwner(
        string leaseStateName,
        string expectedReasonName)
    {
        var leaseState = Enum.Parse<BridgeCutoverLeaseState>(leaseStateName);
        var expectedReason = Enum.Parse<BridgeCutoverFailureReason>(expectedReasonName);
        await using var environment = await IsolatedCutoverEnvironment.StartAsync(leaseState);

        var result = await environment.Coordinator.RunAsync(
            environment.InitialNodeIdentity,
            DotNetInstanceName);

        Assert.AreEqual(BridgeHostCutoverStage.FailedSafe, result.Snapshot.Stage);
        Assert.AreEqual(expectedReason, result.Snapshot.FailureReason);
        Assert.AreEqual(0, environment.DotNetStarts);
        Assert.AreEqual(0, environment.NodeRollbackStarts);
        Assert.AreEqual(0, environment.StartedProcessIds.Count);
        Assert.IsFalse(IsProcessAlive(environment.InitialNodeIdentity.ProcessId));
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

    private sealed class IsolatedCutoverEnvironment : IAsyncDisposable
    {
        private readonly string root;
        private readonly Uri endpoint;
        private readonly BridgeHostCutoverProcessOperations operations;

        private IsolatedCutoverEnvironment(
            string root,
            Uri endpoint,
            BridgeHostCutoverProcessOperations operations,
            BridgeCutoverHostIdentity initialNodeIdentity)
        {
            this.root = root;
            this.endpoint = endpoint;
            this.operations = operations;
            InitialNodeIdentity = initialNodeIdentity;
            Coordinator = new(operations);
        }

        public BridgeHostCutoverCoordinator Coordinator { get; }

        public BridgeCutoverHostIdentity InitialNodeIdentity { get; }

        public List<int> StartedProcessIds { get; } = [];

        public int DotNetStarts { get; private set; }

        public int NodeRollbackStarts { get; private set; }

        public static async Task<IsolatedCutoverEnvironment> StartAsync(
            BridgeCutoverLeaseState leaseState,
            string dotNetInstanceName = DotNetInstanceName)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"ai-cli-feishu-cutover-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var port = FindFreeLoopbackPort();
            var endpoint = new Uri(
                $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/");
            var initialNode = StartFixture(
                root,
                port,
                "node",
                NodeInstanceName);
            try
            {
                await WaitForReadyAsync(endpoint, initialNode);
                IsolatedCutoverEnvironment? environment = null;
                var options = new BridgeHostCutoverProcessOptions(
                    endpoint,
                    ControlToken,
                    new StaticStoreHandoffInspector(new(
                        StoreFlushed: true,
                        StoreCompatible: true,
                        leaseState)),
                    () =>
                    {
                        environment!.NodeRollbackStarts++;
                        return CreateFixtureStartInfo(
                            root,
                            port,
                            "node",
                            NodeInstanceName);
                    },
                    _ =>
                    {
                        environment!.DotNetStarts++;
                        return CreateFixtureStartInfo(
                            root,
                            port,
                            "dotnet",
                            dotNetInstanceName);
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
                environment = new(
                    root,
                    endpoint,
                    operations,
                    new(
                        initialNode.Id,
                        "node",
                        BridgeHostCutoverTransaction.CurrentManagementApiVersion,
                        "active",
                        ActiveOwner: true,
                        NodeInstanceName));
                initialNode.Dispose();
                return environment;
            }
            catch
            {
                await StopFixtureAsync(initialNode.Id, endpoint, "node");
                initialNode.Dispose();
                await DeleteDirectoryWithRetryAsync(root);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var processId in StartedProcessIds.AsEnumerable().Reverse())
            {
                await StopFixtureAsync(processId, endpoint, HostKindFor(processId));
            }
            await StopFixtureAsync(
                InitialNodeIdentity.ProcessId,
                endpoint,
                InitialNodeIdentity.HostKind);
            operations.Dispose();
            await DeleteDirectoryWithRetryAsync(root);
        }

        private string HostKindFor(int processId)
        {
            var index = StartedProcessIds.IndexOf(processId);
            if (NodeRollbackStarts > 0 && index == StartedProcessIds.Count - 1)
            {
                return "node";
            }
            return "dotnet";
        }
    }

    private static Process StartFixture(
        string workingDirectory,
        int port,
        string hostKind,
        string instanceName) =>
        Process.Start(CreateFixtureStartInfo(
            workingDirectory,
            port,
            hostKind,
            instanceName)) ?? throw new InvalidOperationException("Fixture process did not start.");

    private static ProcessStartInfo CreateFixtureStartInfo(
        string workingDirectory,
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
            throw new FileNotFoundException("Cutover Host fixture was not copied to test output.", executable);
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
                    $"Cutover Host fixture exited with code {process.ExitCode}.");
            }
            try
            {
                using var response = await client.GetAsync("health");
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
        throw new TimeoutException("Cutover Host fixture did not become ready.");
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
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
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

    private sealed class StaticStoreHandoffInspector(
        BridgeStoreHandoffEvidence evidence) : IBridgeStoreHandoffInspector
    {
        public ValueTask<BridgeStoreHandoffEvidence> InspectAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(evidence);
        }
    }
}
