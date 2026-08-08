using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishuControl.Tests;

[TestClass]
public sealed class BridgeHostProductionCutoverTests
{
    private const string ValidToken =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void ProductionCompositionRootBuildsBoundLoopbackProcesses()
    {
        using var environment = ProductionEnvironment.Create();
        using var cutover = environment.CreateCutover();

        Assert.AreEqual(environment.Root, cutover.BridgeRoot);
        Assert.AreEqual(environment.ApplicationDirectory, cutover.ApplicationDirectory);
        Assert.AreEqual(environment.DataDirectory, cutover.DataDirectory);
        Assert.AreEqual(9123, cutover.Port);
        Assert.AreEqual(new Uri("http://127.0.0.1:9123/"), cutover.Endpoint);
        Assert.IsInstanceOfType<ProductionBridgeStoreHandoffInspector>(
            cutover.StoreHandoffInspector);

        Assert.AreEqual(BridgeHostMode.NodeProduction, cutover.NodeTarget.Mode);
        Assert.AreEqual("production", cutover.NodeTarget.InstanceName);
        Assert.AreEqual(BridgeHostMode.DotNetProduction, cutover.DotNetTarget.Mode);
        Assert.AreEqual(
            BridgeHostTarget.DotNetProductionInstanceName,
            cutover.DotNetTarget.InstanceName);

        var node = cutover.CreateNodeStartInfo();
        Assert.AreEqual("node.exe", node.FileName);
        Assert.AreEqual(environment.Root, node.WorkingDirectory);
        CollectionAssert.AreEqual(
            new[] { Path.Combine(environment.Root, "dist", "index.js") },
            node.ArgumentList.ToArray());
        Assert.AreEqual("9123", node.Environment["BRIDGE_HTTP_PORT"]);

        var dotNet = cutover.CreateDotNetStartInfo(
            BridgeHostTarget.DotNetProductionInstanceName);
        Assert.AreEqual(
            Path.Combine(
                environment.ApplicationDirectory,
                "AiCliFeishuBridgeHost.exe"),
            dotNet.FileName);
        Assert.AreEqual(environment.Root, dotNet.WorkingDirectory);
        AssertArgumentPair(dotNet, "--data-directory", environment.DataDirectory);
        AssertArgumentPair(dotNet, "--listen", "127.0.0.1");
        AssertArgumentPair(dotNet, "--port", "9123");
        AssertArgumentPair(dotNet, "--ownership", "active");
        AssertArgumentPair(
            dotNet,
            "--instance",
            BridgeHostTarget.DotNetProductionInstanceName);
        CollectionAssert.DoesNotContain(
            dotNet.ArgumentList.ToArray(),
            "--cutover-operation");
        Assert.AreEqual("Major", dotNet.Environment["DOTNET_ROLL_FORWARD"]);
    }

    [TestMethod]
    public void ConstructionDoesNotProbeStartOrMutateProductionState()
    {
        using var environment = ProductionEnvironment.Create();
        var processHandler = new RejectingHandler();
        var recoveryHandler = new RejectingHandler();
        var before = Directory.GetFiles(environment.DataDirectory)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        using (environment.CreateCutover(processHandler, recoveryHandler))
        {
            Assert.AreEqual(0, environment.StartAttempts);
            Assert.AreEqual(0, processHandler.Requests);
            Assert.AreEqual(0, recoveryHandler.Requests);
        }

        var after = Directory.GetFiles(environment.DataDirectory)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(before, after);
        Assert.IsFalse(File.Exists(Path.Combine(
            environment.DataDirectory,
            BridgeHostCutoverCheckpointStore.CheckpointFileName)));
        Assert.IsFalse(File.Exists(Path.Combine(environment.DataDirectory, "owner.json")));
    }

    [TestMethod]
    public void ProductionCompositionRootFailsClosedForInvalidControlTokens()
    {
        using var environment = ProductionEnvironment.Create();
        var invalidTokens = new[]
        {
            new string('a', 63),
            new string('a', 65),
            new string('g', 64),
            $" {ValidToken}",
        };

        foreach (var invalidToken in invalidTokens)
        {
            environment.WriteToken(invalidToken);
            var error = Assert.ThrowsException<InvalidOperationException>(
                () => environment.CreateCutover());
            Assert.IsFalse(
                error.Message.Contains(invalidToken, StringComparison.Ordinal));
            StringAssert.Contains(error.Message, "缺失或格式无效");
        }

        File.WriteAllText(
            Path.Combine(environment.DataDirectory, "control-token.json"),
            "{not-json:" + ValidToken + "}");
        var jsonError = Assert.ThrowsException<InvalidOperationException>(
            () => environment.CreateCutover());
        Assert.IsFalse(
            jsonError.Message.Contains(ValidToken, StringComparison.Ordinal));
    }

    [TestMethod]
    public void ProductionCompositionRootFailsClosedWhenControlTokenIsMissing()
    {
        using var environment = ProductionEnvironment.Create();
        File.Delete(Path.Combine(environment.DataDirectory, "control-token.json"));

        var error = Assert.ThrowsException<InvalidOperationException>(
            () => environment.CreateCutover());

        StringAssert.Contains(error.Message, "缺失或格式无效");
        Assert.IsNull(error.InnerException);
    }

    [TestMethod]
    public void ProductionCompositionRootRejectsUnboundIdentitiesAndInstances()
    {
        using var environment = ProductionEnvironment.Create();
        using var cutover = environment.CreateCutover();

        Assert.ThrowsException<InvalidOperationException>(() =>
            cutover.CreateDotNetStartInfo("another-production"));
        Assert.ThrowsException<ArgumentException>(() =>
            cutover.CutoverAsync(new(
                123,
                "node",
                BridgeHostTarget.CurrentManagementApiVersion,
                "active",
                ActiveOwner: true,
                InstanceName: "another-production")));
    }

    [TestMethod]
    public void DotNetProductionTargetRequiresItsExactInstanceIdentity()
    {
        var target = BridgeHostTarget.DotNetProduction(8765);
        var status = new BridgeStatus
        {
            ProcessId = 123,
            HostKind = "dotnet",
            ManagementApiVersion = BridgeHostTarget.CurrentManagementApiVersion,
            InstanceName = BridgeHostTarget.DotNetProductionInstanceName,
            OwnershipMode = "active",
            ActiveOwner = true,
        };

        Assert.IsTrue(target.Matches(status));
        status.InstanceName = "another-production";
        Assert.IsFalse(target.Matches(status));
        Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostTarget.FromConfiguration("dotnet", 8765));
    }

    private static void AssertArgumentPair(
        ProcessStartInfo startInfo,
        string name,
        string value)
    {
        var arguments = startInfo.ArgumentList.ToArray();
        var index = Array.IndexOf(arguments, name);
        Assert.IsTrue(index >= 0, $"Missing argument {name}.");
        Assert.IsTrue(index + 1 < arguments.Length, $"Missing value for {name}.");
        Assert.AreEqual(value, arguments[index + 1]);
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("Construction must not send HTTP requests.");
        }
    }

    private sealed class ProductionEnvironment : IDisposable
    {
        private readonly string? previousHostPath;

        private ProductionEnvironment(
            string root,
            string applicationDirectory,
            string dataDirectory)
        {
            Root = root;
            ApplicationDirectory = applicationDirectory;
            DataDirectory = dataDirectory;
            previousHostPath = Environment.GetEnvironmentVariable(
                "AI_CLI_FEISHU_DOTNET_HOST_PATH");
            Environment.SetEnvironmentVariable("AI_CLI_FEISHU_DOTNET_HOST_PATH", null);
        }

        public string Root { get; }

        public string ApplicationDirectory { get; }

        public string DataDirectory { get; }

        public int StartAttempts { get; private set; }

        public static ProductionEnvironment Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"ai-cli-feishu-production-cutover-{Guid.NewGuid():N}");
            var applicationDirectory = Path.Combine(root, "app");
            var dataDirectory = Path.Combine(root, "data");
            Directory.CreateDirectory(Path.Combine(root, "dist"));
            Directory.CreateDirectory(applicationDirectory);
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(Path.Combine(root, "dist", "index.js"), "");
            File.WriteAllText(
                Path.Combine(applicationDirectory, "AiCliFeishuBridgeHost.exe"),
                "");
            var environment = new ProductionEnvironment(
                Path.GetFullPath(root),
                Path.GetFullPath(applicationDirectory),
                Path.GetFullPath(dataDirectory));
            environment.WriteToken(ValidToken);
            return environment;
        }

        public BridgeHostProductionCutover CreateCutover(
            HttpMessageHandler? processHandler = null,
            HttpMessageHandler? recoveryHandler = null) =>
            new(
                Root,
                ApplicationDirectory,
                9123,
                startInfo =>
                {
                    StartAttempts++;
                    throw new InvalidOperationException(
                        $"Unexpected process start: {startInfo.FileName}");
                },
                processHandler,
                recoveryHandler);

        public void WriteToken(string token)
        {
            File.WriteAllText(
                Path.Combine(DataDirectory, "control-token.json"),
                JsonSerializer.Serialize(new { token }));
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(
                "AI_CLI_FEISHU_DOTNET_HOST_PATH",
                previousHostPath);
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
