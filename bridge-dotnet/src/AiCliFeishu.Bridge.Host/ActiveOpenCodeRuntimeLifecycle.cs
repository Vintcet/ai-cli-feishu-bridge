using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.OpenCode;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal interface IBridgeOpenCodePortAllocator
{
    ValueTask<int> AllocateAsync(
        IReadOnlySet<int> excludedPorts,
        CancellationToken cancellationToken = default);
}

internal interface IBridgeOpenCodeRuntimeLifecycleOwner :
    IOpenCodeRuntimeLifecycle
{
    ValueTask<BridgeOpenCodeEndpointIdentity> ReserveAsync(
        string cwd,
        string? sessionExternalId,
        CancellationToken cancellationToken = default);

    bool Release(int port);
}

internal sealed class ActiveOpenCodeRuntimeLifecycle :
    IBridgeOpenCodeRuntimeLifecycleOwner,
    IDisposable
{
    private const int MinimumPort = 5_100;
    private const int MaximumPort = 5_999;
    private static readonly TimeSpan DefaultReadyTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(200);
    private readonly object sync = new();
    private readonly BridgeHostOptions options;
    private readonly IBridgeOpenCodeEndpointRegistrationDirectory directory;
    private readonly IManagedRuntimeLifecycle desktopLifecycle;
    private readonly IBridgeOpenCodePortAllocator portAllocator;
    private readonly TimeSpan readyTimeout;
    private readonly TimeSpan pollInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly SemaphoreSlim allocationGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<int, Reservation> reservations = [];
    private bool disposed;

    public ActiveOpenCodeRuntimeLifecycle(
        BridgeHostOptions options,
        IBridgeOpenCodeEndpointRegistrationDirectory directory,
        IManagedRuntimeLifecycle desktopLifecycle)
        : this(
            options,
            directory,
            desktopLifecycle,
            new LoopbackOpenCodePortAllocator(MinimumPort, MaximumPort),
            DefaultReadyTimeout,
            DefaultPollInterval,
            static (duration, cancellationToken) =>
                Task.Delay(duration, cancellationToken))
    {
    }

    internal ActiveOpenCodeRuntimeLifecycle(
        BridgeHostOptions options,
        IBridgeOpenCodeEndpointRegistrationDirectory directory,
        IManagedRuntimeLifecycle desktopLifecycle,
        IBridgeOpenCodePortAllocator portAllocator,
        TimeSpan? readyTimeout = null,
        TimeSpan? pollInterval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
        this.desktopLifecycle = desktopLifecycle ??
            throw new ArgumentNullException(nameof(desktopLifecycle));
        this.portAllocator = portAllocator ??
            throw new ArgumentNullException(nameof(portAllocator));
        this.readyTimeout = readyTimeout ?? DefaultReadyTimeout;
        this.pollInterval = pollInterval ?? DefaultPollInterval;
        if (this.readyTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(readyTimeout));
        }
        if (this.pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }
        this.delay = delay ?? (static (duration, cancellationToken) =>
            Task.Delay(duration, cancellationToken));
    }

    public Task LaunchAsync(
        RuntimeCommandContext context,
        string requestedExternalId,
        string cwd,
        bool elevated,
        CancellationToken cancellationToken = default)
    {
        Prepare(context, cancellationToken);
        requestedExternalId = RequireSessionId(requestedExternalId);
        cwd = NormalizeCwd(cwd);
        return desktopLifecycle.LaunchAsync(
            context,
            RuntimeNames.OpenCode,
            requestedExternalId,
            cwd,
            prompt: null,
            elevated,
            cancellationToken);
    }

    public Task ResumeAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string? cwd,
        CancellationToken cancellationToken = default)
    {
        Prepare(context, cancellationToken);
        sessionExternalId = RequireSessionId(sessionExternalId);
        cwd = cwd is null ? null : NormalizeCwd(cwd);
        return desktopLifecycle.ResumeAsync(
            context,
            RuntimeNames.OpenCode,
            sessionExternalId,
            cwd,
            prompt: null,
            cancellationToken);
    }

    public async Task WaitUntilReadyAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        CancellationToken cancellationToken = default)
    {
        Prepare(context, cancellationToken);
        sessionExternalId = RequireSessionId(sessionExternalId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetime.Token);
        timeout.CancelAfter(readyTimeout);
        try
        {
            while (true)
            {
                ThrowIfDisposed();
                if (directory.FindRegistrationBySession(sessionExternalId) is
                        { Ready: true } target &&
                    directory.IsCurrent(target, sessionExternalId))
                {
                    return;
                }
                await delay(pollInterval, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (Volatile.Read(ref disposed))
        {
            throw new ObjectDisposedException(nameof(ActiveOpenCodeRuntimeLifecycle));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("等待 OpenCode 会话就绪超时。");
        }
    }

    public async Task StopAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        Prepare(context, cancellationToken);
        sessionExternalId = RequireSessionId(sessionExternalId);
        ValidateReason(reason);
        var target = directory.FindRegistrationBySession(sessionExternalId);
        if (target is { Ready: true } && directory.IsCurrent(target, sessionExternalId))
        {
            directory.ForgetSession(target.Port, target.Generation, sessionExternalId);
            return;
        }

        await desktopLifecycle.StopAsync(
            context,
            RuntimeNames.OpenCode,
            sessionExternalId,
            reason,
            cancellationToken);
        if (target is null)
        {
            return;
        }
        if (!ReleaseOwned(target))
        {
            directory.ForgetSession(target.Port, target.Generation, sessionExternalId);
        }
    }

    public async ValueTask<BridgeOpenCodeEndpointIdentity> ReserveAsync(
        string cwd,
        string? sessionExternalId,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable(cancellationToken);
        cwd = NormalizeCwd(cwd);
        sessionExternalId = string.IsNullOrWhiteSpace(sessionExternalId)
            ? null
            : RequireSessionId(sessionExternalId);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetime.Token);
        await allocationGate.WaitAsync(operation.Token);
        try
        {
            ThrowIfDisposed();
            var excluded = directory.ListRegistrations()
                .Select(registration => registration.Port)
                .ToHashSet();
            for (var attempt = MinimumPort; attempt <= MaximumPort; attempt++)
            {
                operation.Token.ThrowIfCancellationRequested();
                var port = await portAllocator.AllocateAsync(excluded, operation.Token);
                if (port is < MinimumPort or > MaximumPort || !excluded.Add(port))
                {
                    throw new InvalidOperationException(
                        "OpenCode 端口分配器返回了无效或重复端口。");
                }
                var identity = directory.TryRegisterAvailable(port, cwd);
                if (identity is null)
                {
                    continue;
                }
                try
                {
                    if (sessionExternalId is not null &&
                        !directory.RememberSession(
                            port,
                            identity.Generation,
                            sessionExternalId))
                    {
                        throw new InvalidOperationException(
                            "OpenCode 恢复会话未能绑定到预留端点。");
                    }
                    lock (sync)
                    {
                        ThrowIfDisposedLocked();
                        reservations.Add(
                            port,
                            new(identity.Generation, sessionExternalId));
                    }
                    return identity;
                }
                catch
                {
                    directory.Unregister(port, identity.Generation);
                    throw;
                }
            }
            throw new InvalidOperationException(
                $"OpenCode 端口池已用尽（{MinimumPort}-{MaximumPort}）。");
        }
        catch (OperationCanceledException) when (Volatile.Read(ref disposed))
        {
            throw new ObjectDisposedException(nameof(ActiveOpenCodeRuntimeLifecycle));
        }
        finally
        {
            allocationGate.Release();
        }
    }

    public bool Release(int port)
    {
        EnsureAvailable(CancellationToken.None);
        if (port is <= 0 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
        Reservation? reservation;
        lock (sync)
        {
            ThrowIfDisposedLocked();
            reservations.Remove(port, out reservation);
        }
        return reservation is null
            ? directory.Unregister(port)
            : directory.Unregister(port, reservation.Generation);
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            reservations.Clear();
        }
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private bool ReleaseOwned(BridgeOpenCodeEndpointIdentity identity)
    {
        lock (sync)
        {
            if (!reservations.TryGetValue(identity.Port, out var reservation) ||
                reservation.Generation != identity.Generation)
            {
                return false;
            }
            reservations.Remove(identity.Port);
        }
        directory.Unregister(identity.Port, identity.Generation);
        return true;
    }

    private void Prepare(
        RuntimeCommandContext context,
        CancellationToken cancellationToken)
    {
        EnsureAvailable(cancellationToken);
        ArgumentNullException.ThrowIfNull(context);
    }

    private void EnsureAvailable(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException(
                "OpenCode 生产生命周期只能用于 Active Host。");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed), this);
    }

    private void ThrowIfDisposedLocked()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static string RequireSessionId(string sessionExternalId) =>
        !string.IsNullOrWhiteSpace(sessionExternalId) &&
        sessionExternalId.Length <= 512 &&
        !sessionExternalId.Any(char.IsControl)
            ? sessionExternalId
            : throw new ArgumentException(
                "OpenCode 会话身份无效。",
                nameof(sessionExternalId));

    private static string NormalizeCwd(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd) ||
            cwd.Length > 32_768 ||
            cwd.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(cwd.Trim()))
        {
            throw new ArgumentException("OpenCode 工作目录无效。", nameof(cwd));
        }
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd.Trim()));
        }
        catch (Exception error) when (
            error is ArgumentException or IOException or NotSupportedException)
        {
            throw new ArgumentException("OpenCode 工作目录无效。", nameof(cwd), error);
        }
    }

    private static void ValidateReason(string? reason)
    {
        if (reason?.Length > 500 || reason?.Any(char.IsControl) == true)
        {
            throw new ArgumentException("OpenCode 停止原因无效。", nameof(reason));
        }
    }

    private sealed record Reservation(long Generation, string? SessionExternalId);
}

internal sealed class LoopbackOpenCodePortAllocator(
    int minimumPort,
    int maximumPort) : IBridgeOpenCodePortAllocator
{
    public ValueTask<int> AllocateAsync(
        IReadOnlySet<int> excludedPorts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(excludedPorts);
        if (minimumPort is <= 0 or > 65_535 ||
            maximumPort < minimumPort ||
            maximumPort > 65_535)
        {
            throw new InvalidOperationException("OpenCode 端口池配置无效。");
        }
        var listeners = IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Select(endpoint => endpoint.Port)
            .ToHashSet();
        for (var port = minimumPort; port <= maximumPort; port++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (excludedPorts.Contains(port) || listeners.Contains(port))
            {
                continue;
            }
            if (CanBind(port))
            {
                return ValueTask.FromResult(port);
            }
        }
        throw new InvalidOperationException(
            $"OpenCode 端口池已用尽（{minimumPort}-{maximumPort}）。");
    }

    private static bool CanBind(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }
}
