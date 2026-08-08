using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class BridgeActiveStartupAuthorizationTests
{
    private string? directory;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            $"bridge-active-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
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
    public void BoundProcessCheckpointAuthorizesActiveStartup()
    {
        WriteCheckpoint("DotNetStartRequested", Environment.ProcessId);

        BridgeActiveStartupGate.Confirm(Options(), Environment.ProcessId);
    }

    [TestMethod]
    public void CompletedCheckpointAuthorizesReplacementProcess()
    {
        WriteCheckpoint("Completed", processId: 81234);

        BridgeActiveStartupGate.Confirm(Options(), processId: 81235);
    }

    [TestMethod]
    public void StoreHandoffCheckpointWaitsForDurableProcessBinding()
    {
        WriteCheckpoint("StoreHandoffVerified", processId: 0);

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeActiveStartupGate.Confirm(Options(), Environment.ProcessId));

        StringAssert.Contains(error.Message, "process-binding-pending");
    }

    [TestMethod]
    public void DifferentProcessCannotReuseUncommittedCheckpoint()
    {
        WriteCheckpoint("DotNetActiveVerified", processId: Environment.ProcessId + 1);

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeActiveStartupGate.Confirm(Options(), Environment.ProcessId));

        StringAssert.Contains(error.Message, "checkpoint-stage-not-authorized");
    }

    [TestMethod]
    public void RollbackCheckpointCannotAuthorizeActiveStartup()
    {
        WriteCheckpoint(
            "RollbackRequired",
            Environment.ProcessId,
            requiresRollback: true,
            failureReason: "OwnershipUncertain");

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeActiveStartupGate.Confirm(Options(), Environment.ProcessId));

        StringAssert.Contains(error.Message, "checkpoint-evidence-mismatch");
    }

    [TestMethod]
    public void DifferentOperationOrInstanceCannotAuthorizeStartup()
    {
        WriteCheckpoint(
            "DotNetStartRequested",
            Environment.ProcessId,
            operationId: "another-operation");
        Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeActiveStartupGate.Confirm(Options(), Environment.ProcessId));

        WriteCheckpoint(
            "DotNetStartRequested",
            Environment.ProcessId,
            instanceName: "another-instance");
        Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeActiveStartupGate.Confirm(Options(), Environment.ProcessId));
    }

    [TestMethod]
    public void MissingOrMalformedCheckpointFailsClosed()
    {
        Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeActiveStartupGate.Confirm(Options(), Environment.ProcessId));

        File.WriteAllText(
            Path.Combine(directory!, BridgeActiveStartupGate.CheckpointFileName),
            "{}");
        Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeActiveStartupGate.Confirm(Options(), Environment.ProcessId));
    }

    [TestMethod]
    public async Task HostedOwnerLeaseRevalidatesCheckpointBeforeAcquiringOwnership()
    {
        WriteCheckpoint("DotNetStartRequested", Environment.ProcessId);
        await using var app = BridgeHostApplication.Build(Options());
        Assert.IsNotNull(
            app.Services.GetService<BridgeActiveStartupAuthorization>());

        WriteCheckpoint(
            "RollbackRequired",
            Environment.ProcessId,
            requiresRollback: true,
            failureReason: "OwnershipUncertain");

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            app.StartAsync());
        Assert.IsFalse(Directory.Exists(Path.Combine(
            directory!,
            "bridge-active-owner.lock")));
    }

    private BridgeHostOptions Options() => new(
        directory!,
        IPAddress.Loopback,
        0,
        BridgeOwnershipMode.Active,
        "production-dotnet")
    {
        CutoverOperationId = "operation-1",
    };

    private void WriteCheckpoint(
        string stage,
        int processId,
        bool requiresRollback = false,
        string failureReason = "None",
        string operationId = "operation-1",
        string instanceName = "production-dotnet")
    {
        var checkpoint = new
        {
            schemaVersion = 1,
            operationId,
            updatedAt = DateTimeOffset.Parse("2026-08-08T08:00:00.0000000+00:00"),
            stage,
            requiresRollback,
            failureReason,
            expectedNode = new
            {
                processId = 81111,
                hostKind = "node",
                managementApiVersion = 1,
                ownershipMode = "active",
                activeOwner = true,
                instanceName = "production",
            },
            expectedDotNetInstanceName = instanceName,
            dotNetProcessId = processId,
            nodeRollbackProcessId = 0,
        };
        File.WriteAllText(
            Path.Combine(directory!, BridgeActiveStartupGate.CheckpointFileName),
            JsonSerializer.Serialize(checkpoint));
    }
}
