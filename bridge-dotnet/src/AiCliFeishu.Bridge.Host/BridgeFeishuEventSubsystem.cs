using AiCliFeishu.Bridge.Adapters.Feishu;

namespace AiCliFeishu.Bridge.Host;

/// <summary>
/// Hosts the Feishu event pump after the standard intent boundary is ready.
/// Passive mode completes without I/O; Active mode owns one cancellable pump task.
/// </summary>
public sealed class BridgeFeishuEventSubsystem(
    FeishuEventPump eventPump,
    BridgeHostOptions options) :
    IBridgeHostSubsystem,
    IBridgeHostSubsystemHealth,
    IBridgeBackgroundSubsystem
{
    private readonly object sync = new();
    private CancellationTokenSource? shutdown;
    private Task? running;
    private bool started;
    private Exception? fault;

    public string Name => "feishu-event-pump";

    public BridgeComponentHealth ComponentHealth
    {
        get
        {
            lock (sync)
            {
                if (fault is not null)
                {
                    return new(Name, "failed", "event-pump-faulted");
                }
                if (!started)
                {
                    return new(Name, "starting");
                }
                return options.OwnershipMode is BridgeOwnershipMode.Passive
                    ? new(Name, "passive", "event-source-disabled")
                    : new(Name, "ready", "event-source-active");
            }
        }
    }

    public Task? Completion
    {
        get
        {
            lock (sync)
            {
                return options.OwnershipMode is BridgeOwnershipMode.Active
                    ? running
                    : null;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.OwnershipMode is BridgeOwnershipMode.Passive)
        {
            await eventPump.RunAsync(cancellationToken);
            lock (sync)
            {
                started = true;
            }
            return;
        }
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException("未知的飞书事件泵所有权模式。");
        }
        lock (sync)
        {
            if (started)
            {
                throw new InvalidOperationException("飞书事件泵已经启动。");
            }
            shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            fault = null;
            started = true;
            running = RunActiveAsync(shutdown.Token);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? cancellation;
        Task? completion;
        lock (sync)
        {
            if (!started)
            {
                return;
            }
            cancellation = shutdown;
            completion = running;
            started = false;
        }
        cancellation?.Cancel();
        try
        {
            if (completion is not null)
            {
                try
                {
                    await completion.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (
                    cancellation?.IsCancellationRequested == true)
                {
                }
                catch when (completion.IsFaulted)
                {
                    _ = completion.Exception;
                }
            }
        }
        finally
        {
            cancellation?.Dispose();
            lock (sync)
            {
                shutdown = null;
                running = null;
                fault = null;
            }
        }
    }

    private async Task RunActiveAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            await eventPump.RunAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                "Active 飞书事件源在停止信号前意外结束。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            lock (sync)
            {
                fault = error;
            }
            throw;
        }
    }
}
