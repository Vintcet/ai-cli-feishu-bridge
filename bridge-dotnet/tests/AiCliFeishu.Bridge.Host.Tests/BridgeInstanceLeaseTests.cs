namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class BridgeInstanceLeaseTests
{
    private string? directory;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(Path.GetTempPath(), $"ai-cli-feishu-host-{Guid.NewGuid():N}");
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
    public async Task RejectsASecondHostForTheSameInstance()
    {
        var options = BridgeHostOptions.Passive(directory!, port: 0);
        await using var first = new FileBridgeInstanceLease(options);
        await using var second = new FileBridgeInstanceLease(options);
        await first.AcquireAsync();

        await Assert.ThrowsExceptionAsync<BridgeInstanceAlreadyRunningException>(async () =>
            await second.AcquireAsync());
    }

    [TestMethod]
    public async Task LeaseCanBeReacquiredAfterGracefulRelease()
    {
        var options = BridgeHostOptions.Passive(directory!, port: 0);
        await using (var first = new FileBridgeInstanceLease(options))
        {
            await first.AcquireAsync();
        }
        await using var second = new FileBridgeInstanceLease(options);

        await second.AcquireAsync();

        Assert.IsTrue(second.IsHeld);
    }

    [TestMethod]
    public async Task DifferentInstanceNamesHaveIndependentLeases()
    {
        var leftOptions = BridgeHostOptions.Passive(directory!, port: 0) with { InstanceName = "left" };
        var rightOptions = leftOptions with { InstanceName = "right" };
        await using var left = new FileBridgeInstanceLease(leftOptions);
        await using var right = new FileBridgeInstanceLease(rightOptions);

        await left.AcquireAsync();
        await right.AcquireAsync();

        Assert.IsTrue(left.IsHeld);
        Assert.IsTrue(right.IsHeld);
    }
}
