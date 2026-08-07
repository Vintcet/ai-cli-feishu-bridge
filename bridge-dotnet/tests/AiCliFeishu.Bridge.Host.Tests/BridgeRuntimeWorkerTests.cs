using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class BridgeRuntimeWorkerTests
{
    [TestMethod]
    public async Task StartsInOrderAndStopsInReverseOrder()
    {
        var operations = new List<string>();
        var health = new BridgeHealthRegistry(BridgeHostOptions.Passive(Path.GetTempPath()));
        var lifetime = new RecordingApplicationLifetime();
        var worker = new BridgeRuntimeWorker(
            [
                new RecordingSubsystem("first", operations),
                new RecordingSubsystem("second", operations),
            ],
            health,
            lifetime,
            NullLogger<BridgeRuntimeWorker>.Instance);
        using var cancellation = new CancellationTokenSource();

        await worker.StartAsync(cancellation.Token);
        await WaitUntilAsync(() => health.Snapshot().Status == "ready");
        Assert.AreEqual("ready", health.Snapshot().Status);
        await worker.StopAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "start:first", "start:second", "stop:second", "stop:first" },
            operations);
        Assert.AreEqual("stopped", health.Snapshot().Status);
    }

    [TestMethod]
    public async Task StartupFailureCleansUpAndRemainsFaulted()
    {
        var operations = new List<string>();
        var health = new BridgeHealthRegistry(BridgeHostOptions.Passive(Path.GetTempPath()));
        var lifetime = new RecordingApplicationLifetime();
        var worker = new BridgeRuntimeWorker(
            [
                new RecordingSubsystem("started", operations),
                new RecordingSubsystem("broken", operations, failOnStart: true),
            ],
            health,
            lifetime,
            NullLogger<BridgeRuntimeWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await worker.ExecuteTask!);

        CollectionAssert.AreEqual(
            new[] { "start:started", "start:broken", "stop:started" },
            operations);
        Assert.AreEqual("faulted", health.Snapshot().Status);
        Assert.IsTrue(lifetime.StopRequested);
    }

    [TestMethod]
    public async Task BackgroundSubsystemFailureFaultsHostAndCleansUpInReverseOrder()
    {
        var operations = new List<string>();
        var health = new BridgeHealthRegistry(BridgeHostOptions.Passive(Path.GetTempPath()));
        var lifetime = new RecordingApplicationLifetime();
        var background = new RecordingBackgroundSubsystem("background", operations);
        var worker = new BridgeRuntimeWorker(
            [
                new RecordingSubsystem("first", operations),
                background,
            ],
            health,
            lifetime,
            NullLogger<BridgeRuntimeWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => health.Snapshot().Status == "ready");
        background.Fail();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await worker.ExecuteTask!);

        CollectionAssert.AreEqual(
            new[]
            {
                "start:first",
                "start:background",
                "stop:background",
                "stop:first",
            },
            operations);
        Assert.AreEqual("faulted", health.Snapshot().Status);
        Assert.IsTrue(lifetime.StopRequested);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class RecordingSubsystem(
        string name,
        List<string> operations,
        bool failOnStart = false) : IBridgeHostSubsystem
    {
        public string Name => name;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            operations.Add($"start:{name}");
            return failOnStart
                ? Task.FromException(new InvalidOperationException("synthetic failure"))
                : Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            operations.Add($"stop:{name}");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingBackgroundSubsystem(
        string name,
        List<string> operations) :
        IBridgeHostSubsystem,
        IBridgeBackgroundSubsystem
    {
        private readonly TaskCompletionSource completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => name;

        public Task? Completion => completion.Task;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            operations.Add($"start:{name}");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            operations.Add($"stop:{name}");
            return Task.CompletedTask;
        }

        public void Fail() => completion.TrySetException(
            new InvalidOperationException("synthetic background failure"));
    }

    private sealed class RecordingApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource started = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource stopped = new();

        public CancellationToken ApplicationStarted => started.Token;

        public CancellationToken ApplicationStopping => stopping.Token;

        public CancellationToken ApplicationStopped => stopped.Token;

        public bool StopRequested { get; private set; }

        public void StopApplication()
        {
            StopRequested = true;
            stopping.Cancel();
        }
    }
}
