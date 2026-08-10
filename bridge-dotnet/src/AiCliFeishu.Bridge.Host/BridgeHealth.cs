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
    private readonly Dictionary<string, IBridgeHostSubsystemHealth> liveComponents =
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

    public void Track(IBridgeHostSubsystemHealth provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var component = provider.ComponentHealth;
        ArgumentException.ThrowIfNullOrWhiteSpace(component.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(component.Status);
        lock (sync)
        {
            components[component.Name] = component;
            liveComponents[component.Name] = provider;
        }
    }

    public BridgeHealthSnapshot Snapshot()
    {
        BridgeHostLifecycleState currentLifecycle;
        Dictionary<string, BridgeComponentHealth> currentComponents;
        KeyValuePair<string, IBridgeHostSubsystemHealth>[] currentProviders;
        lock (sync)
        {
            currentLifecycle = lifecycle;
            currentComponents = new Dictionary<string, BridgeComponentHealth>(
                components,
                StringComparer.Ordinal);
            currentProviders = liveComponents.ToArray();
        }

        foreach (var (name, provider) in currentProviders)
        {
            try
            {
                var component = provider.ComponentHealth;
                currentComponents[name] = string.Equals(
                    component.Name,
                    name,
                    StringComparison.Ordinal)
                    ? component
                    : component with { Name = name };
            }
            catch (Exception error)
            {
                currentComponents[name] = new(
                    name,
                    "failed",
                    $"health-read-{error.GetType().Name}");
            }
        }

        var ok = currentLifecycle is BridgeHostLifecycleState.Ready &&
            currentComponents.Values.All(component =>
                component.Status is "ready" or "healthy" or "passive");
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0.0";
        return new(
            ok,
            BridgeHostManagementContract.HostKind,
            BridgeHostManagementContract.ApiVersion,
            options.InstanceName,
            currentLifecycle.ToString().ToLowerInvariant(),
            version,
            Environment.ProcessId,
            StartedAt,
            options.OwnershipMode.ToString().ToLowerInvariant(),
            options.OwnershipMode is BridgeOwnershipMode.Active,
            currentComponents.Values
                .OrderBy(component => component.Name, StringComparer.Ordinal)
                .ToArray());
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

public interface IBridgeBackgroundSubsystem
{
    Task? Completion { get; }
}

public sealed class BridgeRuntimeWorker(
    Func<IReadOnlyList<IBridgeHostSubsystem>> resolveSubsystems,
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
            // BackgroundService.StartAsync waits for ExecuteAsync to reach its
            // first await. Yield before resolving the complete production graph
            // so Kestrel and the preceding lease services can finish starting.
            await Task.Yield();
            // Hosted services are resolved before any of them starts. Resolve the
            // production graph here so the instance and Active Owner leases have
            // already been acquired by the preceding hosted services.
            foreach (var subsystem in resolveSubsystems())
            {
                await subsystem.StartAsync(stoppingToken);
                started.Add(subsystem);
                if (subsystem is IBridgeHostSubsystemHealth provider)
                {
                    health.Track(provider);
                }
                else
                {
                    health.Report(subsystem.Name, "ready");
                }
            }
            health.SetLifecycle(BridgeHostLifecycleState.Ready);
            await AwaitShutdownOrBackgroundFailureAsync(stoppingToken);
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

    private async Task AwaitShutdownOrBackgroundFailureAsync(
        CancellationToken stoppingToken)
    {
        var completions = started
            .OfType<IBridgeBackgroundSubsystem>()
            .Select(subsystem => subsystem.Completion)
            .Where(completion => completion is not null)
            .Cast<Task>()
            .ToArray();
        if (completions.Length == 0)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            return;
        }

        var shutdown = Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        var completed = await Task.WhenAny(completions.Append(shutdown));
        stoppingToken.ThrowIfCancellationRequested();
        if (ReferenceEquals(completed, shutdown))
        {
            await shutdown;
            return;
        }
        await completed;
        throw new InvalidOperationException(
            "Bridge Host 后台子系统在停止信号前意外退出。");
    }
}
