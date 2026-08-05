using System.Text.Json;

namespace AiCliFeishu.Bridge.Host;

public interface IBridgeInstanceLease : IAsyncDisposable
{
    string LockFilePath { get; }

    bool IsHeld { get; }

    ValueTask AcquireAsync(CancellationToken cancellationToken = default);
}

public sealed class FileBridgeInstanceLease(
    BridgeHostOptions options,
    TimeProvider? timeProvider = null) : IBridgeInstanceLease
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private FileStream? stream;

    public string LockFilePath { get; } = Path.Combine(
        options.DataDirectory,
        $"bridge-host-{options.InstanceName}.lock");

    public bool IsHeld => stream is not null;

    public async ValueTask AcquireAsync(CancellationToken cancellationToken = default)
    {
        if (stream is not null)
        {
            throw new InvalidOperationException("Bridge Host 单实例租约已取得。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(LockFilePath)!);
        FileStream candidate;
        try
        {
            candidate = new FileStream(
                LockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                4_096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new BridgeInstanceAlreadyRunningException(LockFilePath, error);
        }

        try
        {
            var metadata = JsonSerializer.SerializeToUtf8Bytes(new
            {
                processId = Environment.ProcessId,
                acquiredAt = clock.GetUtcNow(),
                ownershipMode = options.OwnershipMode.ToString().ToLowerInvariant(),
            });
            candidate.SetLength(0);
            await candidate.WriteAsync(metadata, cancellationToken);
            await candidate.FlushAsync(cancellationToken);
            stream = candidate;
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        var held = Interlocked.Exchange(ref stream, null);
        if (held is null)
        {
            return ValueTask.CompletedTask;
        }
        held.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class BridgeInstanceAlreadyRunningException(string lockFilePath, Exception innerException)
    : InvalidOperationException(
        $"Bridge Host 实例已在运行，无法取得单实例租约：{lockFilePath}",
        innerException)
{
    public string LockFilePath { get; } = lockFilePath;
}

public sealed class BridgeInstanceLeaseService(IBridgeInstanceLease lease) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        lease.AcquireAsync(cancellationToken).AsTask();

    public Task StopAsync(CancellationToken cancellationToken) =>
        lease.DisposeAsync().AsTask();
}
