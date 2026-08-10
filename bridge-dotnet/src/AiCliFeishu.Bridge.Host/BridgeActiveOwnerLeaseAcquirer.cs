using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishu.Bridge.Host;

internal sealed class ActiveOwnerLeaseAcquirer : IBridgeActiveOwnerLeaseLifecycle
{
    private const int MaximumAcquireAttempts = 8;

    private readonly string dataDirectory;
    private readonly ActiveOwnerLeaseObserver observer;
    private readonly ActiveOwnerLeaseRecord record;
    private FileStream? ownershipHandle;
    private bool held;

    public ActiveOwnerLeaseAcquirer(BridgeHostOptions options)
        : this(
            ActiveDataDirectory(options),
            options.InstanceName,
            Environment.ProcessId)
    {
    }

    internal ActiveOwnerLeaseAcquirer(
        string dataDirectory,
        string instanceName,
        int processId,
        TimeProvider? timeProvider = null,
        Func<int, bool>? processAlive = null,
        Func<string>? leaseIdFactory = null)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("Active Owner 数据目录不能为空。", nameof(dataDirectory));
        }
        if (!IsAsciiToken(instanceName))
        {
            throw new ArgumentException(
                "Active Owner 实例名只能包含 ASCII 字母、数字、连字符和下划线。",
                nameof(instanceName));
        }
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(processId),
                "Active Owner 进程号必须是正整数。");
        }

        var leaseId = (leaseIdFactory ?? (() => Guid.NewGuid().ToString("N")))();
        if (!IsAsciiToken(leaseId))
        {
            throw new InvalidOperationException(
                "Active Owner leaseId 只能包含 ASCII 字母、数字、连字符和下划线。");
        }

        this.dataDirectory = Path.GetFullPath(dataDirectory);
        observer = processAlive is null
            ? new ActiveOwnerLeaseObserver(this.dataDirectory)
            : new ActiveOwnerLeaseObserver(this.dataDirectory, processAlive);
        record = new ActiveOwnerLeaseRecord(
            ActiveOwnerLeaseObserver.SchemaVersion,
            "dotnet",
            "active",
            processId,
            instanceName,
            leaseId,
            (timeProvider ?? TimeProvider.System).GetUtcNow());
    }

    internal string LockDirectoryPath => observer.LockDirectoryPath;

    internal string MetadataPath => observer.MetadataPath;

    internal ActiveOwnerLeaseRecord Record => record;

    public bool IsHeld => held;

    public ActiveOwnerLeaseRecord? HeldLease => held ? record : null;

    public async ValueTask<ActiveOwnerLeaseRecord> AcquireAsync(
        CancellationToken cancellationToken = default)
    {
        if (held)
        {
            throw new InvalidOperationException("Active Owner 租约已取得。");
        }

        Directory.CreateDirectory(dataDirectory);
        var stagingDirectory = Path.Combine(
            dataDirectory,
            $"bridge-active-owner.pending-{record.LeaseId}");
        try
        {
            await PrepareStagingDirectoryAsync(
                stagingDirectory,
                cancellationToken);
            for (var attempt = 0; attempt < MaximumAcquireAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = await observer.InspectAsync(cancellationToken);
                switch (snapshot.State)
                {
                    case ActiveOwnerLeaseState.Invalid:
                        throw InvalidLease();
                    case ActiveOwnerLeaseState.Live:
                        throw AlreadyOwned(snapshot.Record!);
                    case ActiveOwnerLeaseState.Stale:
                        _ = TryQuarantineStaleLease(snapshot.Record!);
                        continue;
                    case ActiveOwnerLeaseState.Missing:
                        break;
                    default:
                        throw new InvalidOperationException("未知的 Active Owner 租约状态。");
                }

                try
                {
                    Directory.Move(stagingDirectory, LockDirectoryPath);
                    try
                    {
                        ownershipHandle = OpenOwnershipHandle();
                    }
                    catch
                    {
                        TryDeleteOwnedLeaseDirectory();
                        throw;
                    }
                    held = true;
                    return record;
                }
                catch (IOException error)
                {
                    var afterConflict = await observer.InspectAsync(cancellationToken);
                    if (afterConflict.State is ActiveOwnerLeaseState.Missing)
                    {
                        throw new InvalidOperationException(
                            "无法原子发布 Active Owner 租约。",
                            error);
                    }
                }
            }
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }

        throw new InvalidOperationException(
            "Active Owner 租约在取得期间反复变化，已中止切换。");
    }

    public async ValueTask ReleaseAsync(
        CancellationToken cancellationToken = default)
    {
        if (!held)
        {
            return;
        }

        var snapshot = await observer.InspectAsync(cancellationToken);
        if (snapshot.Record?.LeaseId != record.LeaseId)
        {
            if (ownershipHandle is not null)
            {
                await ownershipHandle.DisposeAsync();
                ownershipHandle = null;
            }
            held = false;
            throw new InvalidOperationException(
                "Active Owner 租约身份已变化，拒绝删除其他 Owner 的租约。");
        }

        if (ownershipHandle is not null)
        {
            await ownershipHandle.DisposeAsync();
            ownershipHandle = null;
        }
        held = false;
        Directory.Delete(LockDirectoryPath, recursive: true);
    }

    public ValueTask DisposeAsync() => ReleaseAsync();

    private static string ActiveDataDirectory(BridgeHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException(
                "Active Owner 租约生命周期只能用于 Active Host。");
        }
        return options.DataDirectory;
    }

    private async Task PrepareStagingDirectoryAsync(
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagingDirectory);
        var metadataPath = Path.Combine(
            stagingDirectory,
            ActiveOwnerLeaseObserver.MetadataFileName);
        var metadata = JsonSerializer.SerializeToUtf8Bytes(
            record,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await using var stream = new FileStream(
            metadataPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            4_096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(metadata, cancellationToken);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private FileStream OpenOwnershipHandle() => new(
        Path.Combine(LockDirectoryPath, ActiveOwnerLeaseObserver.OwnershipHandleFileName),
        FileMode.CreateNew,
        FileAccess.ReadWrite,
        FileShare.Read,
        1,
        FileOptions.WriteThrough);

    private void TryDeleteOwnedLeaseDirectory()
    {
        try
        {
            if (!File.Exists(MetadataPath))
            {
                return;
            }
            var current = JsonSerializer.Deserialize<ActiveOwnerLeaseRecord>(
                File.ReadAllText(MetadataPath),
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = false,
                });
            if (current?.LeaseId == record.LeaseId)
            {
                Directory.Delete(LockDirectoryPath, recursive: true);
            }
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    private bool TryQuarantineStaleLease(ActiveOwnerLeaseRecord staleRecord)
    {
        var staleDirectory = Path.Combine(
            dataDirectory,
            $"bridge-active-owner.stale-{staleRecord.LeaseId}");
        try
        {
            Directory.Move(LockDirectoryPath, staleDirectory);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException) when (
            Directory.Exists(staleDirectory) ||
            !Directory.Exists(LockDirectoryPath))
        {
            return false;
        }
    }

    private static InvalidOperationException InvalidLease() => new(
        "Active Owner 租约路径或元数据无效，拒绝自动抢占。");

    private static InvalidOperationException AlreadyOwned(ActiveOwnerLeaseRecord owner) => new(
        $"生产 Store 已由 {owner.HostKind} Active Owner 持有（pid={owner.ProcessId}）。");

    private static bool IsAsciiToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class ActiveOwnerLeaseHostedService(
    IBridgeActiveOwnerLeaseLifecycle lease,
    BridgeHealthRegistry health) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await lease.AcquireAsync(cancellationToken);
            health.Report("production-owner", "ready", "active-owner-dotnet-held");
        }
        catch
        {
            health.Report("production-owner", "failed", "active-owner-lease-not-held");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        try
        {
            await lease.ReleaseAsync(CancellationToken.None);
            health.Report("production-owner", "stopped");
        }
        catch
        {
            health.Report("production-owner", "failed", "active-owner-lease-release-failed");
            throw;
        }
    }
}
