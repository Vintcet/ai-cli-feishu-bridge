using System.Reflection;

namespace AiCliFeishu.Bridge.Host;

public enum BridgeHostLifecycleState
{
    Starting,
    Ready,
    Stopping,
    Stopped,
    Faulted,
}

public sealed record BridgeComponentHealth(
    string Name,
    string Status,
    string? Detail = null);

public sealed record BridgeHealthSnapshot(
    bool Ok,
    string HostKind,
    int ManagementApiVersion,
    string InstanceName,
    string Status,
    string Version,
    int ProcessId,
    DateTimeOffset StartedAt,
    string OwnershipMode,
    bool ActiveOwner,
    IReadOnlyList<BridgeComponentHealth> Components);

public sealed class BridgeHealthRegistry(
    BridgeHostOptions options,
    TimeProvider? timeProvider = null)
{
    private readonly object sync = new();
    private readonly Dictionary<string, BridgeComponentHealth> components =
        new(StringComparer.Ordinal);
    private BridgeHostLifecycleState lifecycle = BridgeHostLifecycleState.Starting;

    public DateTimeOffset StartedAt { get; } =
        (timeProvider ?? TimeProvider.System).GetUtcNow();

    public void SetLifecycle(BridgeHostLifecycleState state)
    {
        lock (sync)
        {
            lifecycle = state;
        }
    }

    public void Report(string name, string status, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        lock (sync)
        {
            components[name] = new(name, status, detail);
        }
    }

    public BridgeHealthSnapshot Snapshot()
    {
        lock (sync)
        {
            var ok = lifecycle is BridgeHostLifecycleState.Ready &&
                components.Values.All(component => component.Status is "ready" or "passive");
            var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0.0";
            return new(
                ok,
                BridgeHostManagementContract.HostKind,
                BridgeHostManagementContract.ApiVersion,
                options.InstanceName,
                lifecycle.ToString().ToLowerInvariant(),
                version,
                Environment.ProcessId,
                StartedAt,
                options.OwnershipMode.ToString().ToLowerInvariant(),
                options.OwnershipMode is BridgeOwnershipMode.Active,
                components.Values.OrderBy(component => component.Name, StringComparer.Ordinal).ToArray());
        }
    }
}

public interface IBridgeHostSubsystem
{
    string Name { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

public interface IBridgeHostSubsystemHealth
{
    BridgeComponentHealth ComponentHealth { get; }
}

public sealed class PassiveOwnerGuardSubsystem : IBridgeHostSubsystem
{
    public string Name => "production-owner";

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class BridgeRuntimeWorker(
    IEnumerable<IBridgeHostSubsystem> subsystems,
    BridgeHealthRegistry health,
    IHostApplicationLifetime applicationLifetime,
    ILogger<BridgeRuntimeWorker> logger) : BackgroundService
{
    private readonly List<IBridgeHostSubsystem> started = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var faulted = false;
        try
        {
            foreach (var subsystem in subsystems)
            {
                await subsystem.StartAsync(stoppingToken);
                started.Add(subsystem);
                var component = subsystem is IBridgeHostSubsystemHealth provider
                    ? provider.ComponentHealth
                    : new BridgeComponentHealth(
                        subsystem.Name,
                        subsystem is PassiveOwnerGuardSubsystem ? "passive" : "ready");
                health.Report(component.Name, component.Status, component.Detail);
            }
            health.SetLifecycle(BridgeHostLifecycleState.Ready);
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            faulted = true;
            health.SetLifecycle(BridgeHostLifecycleState.Faulted);
            health.Report("host-runtime", "failed", error.GetType().Name);
            logger.LogCritical(error, "Bridge Host 后台生命周期启动失败。");
            applicationLifetime.StopApplication();
            throw;
        }
        finally
        {
            if (!faulted)
            {
                health.SetLifecycle(BridgeHostLifecycleState.Stopping);
            }
            for (var index = started.Count - 1; index >= 0; index--)
            {
                try
                {
                    await started[index].StopAsync(CancellationToken.None);
                }
                catch (Exception error)
                {
                    logger.LogError(error, "停止子系统 {Subsystem} 失败。", started[index].Name);
                }
            }
            health.SetLifecycle(
                faulted
                    ? BridgeHostLifecycleState.Faulted
                    : BridgeHostLifecycleState.Stopped);
        }
    }
}
