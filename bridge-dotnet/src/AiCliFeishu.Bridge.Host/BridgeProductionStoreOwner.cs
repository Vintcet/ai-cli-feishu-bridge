using System.Security.Cryptography;
using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishu.Bridge.Host;

internal enum BridgeProductionStoreState
{
    NotOpened,
    Open,
    Failed,
    Closed,
}

internal sealed record BridgeProductionStoreSnapshot(
    BridgeProductionStoreState State,
    BridgeStoreSnapshot? Store,
    int StoreFiles,
    string? FailureDetail = null,
    string? AuditFailureDetail = null);

internal interface IBridgeProductionStoreOwner
{
    BridgeProductionStoreSnapshot Snapshot { get; }

    ValueTask OpenAsync(CancellationToken cancellationToken = default);

    ValueTask<BridgeStoreSnapshot> ReadAsync(
        CancellationToken cancellationToken = default);

    ValueTask FlushAsync(CancellationToken cancellationToken = default);

    ValueTask UpdateAsync(
        Func<BridgeStoreSnapshot, BridgeStoreSnapshot> update,
        CancellationToken cancellationToken = default);

    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}

internal interface IBridgeProductionStoreProjectionReader
{
    ValueTask<BridgeStoreSnapshot> ReadForProjectionAsync(
        CancellationToken cancellationToken = default);
}

internal sealed class ActiveProductionStoreOwner :
    IBridgeProductionStoreOwner,
    IBridgeProductionStoreProjectionReader,
    IBridgeControlStoreStatusSource,
    IBridgeHostSubsystem,
    IBridgeHostSubsystemHealth
{
    private readonly BridgeHostOptions options;
    private readonly Func<CancellationToken, ValueTask<ActiveOwnerLeaseSnapshot>> inspectOwnerLease;
    private readonly IBridgeActiveOwnerLeaseLifecycle ownerLease;
    private readonly SemaphoreSlim lifecycleLock = new(1, 1);
    private readonly BridgeJsonStoreRepository repository;
    private readonly BridgeJsonStoreRepository projectionRepository;
    private readonly IApprovalAuditLog? approvalAudit;
    private BridgeProductionStoreSnapshot snapshot = new(
        BridgeProductionStoreState.NotOpened,
        null,
        0);

    public ActiveProductionStoreOwner(
        BridgeHostOptions options,
        IBridgeActiveOwnerLeaseLifecycle ownerLease,
        IApprovalAuditLog? approvalAudit = null)
        : this(
            options,
            ownerLease,
            CreateRepository(options),
            new ActiveOwnerLeaseObserver(options.DataDirectory).InspectAsync,
            approvalAudit)
    {
    }

    internal ActiveProductionStoreOwner(
        BridgeHostOptions options,
        IBridgeActiveOwnerLeaseLifecycle ownerLease,
        BridgeJsonStoreRepository repository,
        Func<CancellationToken, ValueTask<ActiveOwnerLeaseSnapshot>>? inspectOwnerLease = null,
        IApprovalAuditLog? approvalAudit = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ownerLease);
        ArgumentNullException.ThrowIfNull(repository);
        EnsureActive(options);
        if (repository.Access is not BridgeStoreAccess.ReadWriteActiveOwner)
        {
            throw new InvalidOperationException(
                "生产 Store Owner 必须使用 Active Owner 专用写入 Repository。");
        }
        this.options = options;
        this.ownerLease = ownerLease;
        this.repository = repository;
        this.approvalAudit = approvalAudit;
        projectionRepository = new(
            options.DataDirectory,
            BridgeStoreAccess.ReadOnly);
        this.inspectOwnerLease = inspectOwnerLease ??
            new ActiveOwnerLeaseObserver(options.DataDirectory).InspectAsync;
    }

    public string Name => "production-store";

    public BridgeProductionStoreSnapshot Snapshot => Redact(Volatile.Read(ref snapshot));

    BridgeControlStoreStatus IBridgeControlStoreStatusSource.Status =>
        BridgeControlStoreStatusProjection.FromProduction(CurrentSnapshot);

    Task IBridgeControlStoreStatusSource.RefreshAsync(
        CancellationToken cancellationToken)
    {
        // The active Store owner is already the sole in-process authority. A
        // status refresh must not perform a second disk read or replace its
        // current projection while runtime events may be committing updates.
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    internal BridgeProductionStoreSnapshot CurrentSnapshot => Volatile.Read(ref snapshot);

    public BridgeComponentHealth ComponentHealth
    {
        get
        {
            var current = CurrentSnapshot;
            return current.State switch
            {
                BridgeProductionStoreState.Open when current.AuditFailureDetail is not null => new(
                    Name,
                    "failed",
                    current.AuditFailureDetail),
                BridgeProductionStoreState.Open => new(
                    Name,
                    "ready",
                    $"loaded files={current.StoreFiles}"),
                BridgeProductionStoreState.Failed => new(
                    Name,
                    "failed",
                    current.FailureDetail ?? "store-operation-failed"),
                BridgeProductionStoreState.Closed => new(Name, "stopped"),
                _ => new(Name, "starting"),
            };
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        OpenAsync(cancellationToken).AsTask();

    public Task StopAsync(CancellationToken cancellationToken) =>
        CloseAsync(CancellationToken.None).AsTask();

    public async ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            var current = CurrentSnapshot;
            if (current.State is BridgeProductionStoreState.Open)
            {
                return;
            }
            await RequireOwnerLeaseAsync(cancellationToken);
            try
            {
                var store = await LoadStoreWithRetryAsync(cancellationToken);
                var bootstrapped = Bootstrap(store);
                if (!ReferenceEquals(bootstrapped, store) ||
                    !repository.HasCommittedGeneration)
                {
                    await WriteStoreWithRetryAsync(bootstrapped, cancellationToken);
                    store = bootstrapped;
                }
                await RequireOwnerLeaseAsync(cancellationToken);
                Volatile.Write(
                    ref snapshot,
                    new BridgeProductionStoreSnapshot(
                        BridgeProductionStoreState.Open,
                        store,
                        ExistingStoreFiles(options.DataDirectory)));
            }
            catch (Exception error)
            {
                Volatile.Write(
                    ref snapshot,
                    new BridgeProductionStoreSnapshot(
                        BridgeProductionStoreState.Failed,
                        null,
                        ExistingStoreFiles(options.DataDirectory),
                        Failure("open", error)));
                throw;
            }
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            var current = RequireOpenStore();
            await RequireOwnerLeaseAsync(cancellationToken);
            await WriteStoreWithRetryAsync(current.Store!, cancellationToken);
            await RequireOwnerLeaseAsync(cancellationToken);
            Volatile.Write(
                ref snapshot,
                current with { StoreFiles = ExistingStoreFiles(options.DataDirectory) });
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public async ValueTask<BridgeStoreSnapshot> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await RequireOwnerLeaseAsync(cancellationToken);
            var store = RequireOpenStore().Store!;
            await RequireOwnerLeaseAsync(cancellationToken);
            return store;
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public async ValueTask<BridgeStoreSnapshot> ReadForProjectionAsync(
        CancellationToken cancellationToken = default)
    {
        await lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            var current = CurrentSnapshot;
            if (current.Store is not null)
            {
                return current.Store;
            }

            Exception? lastError = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return await projectionRepository.LoadAsync(cancellationToken);
                }
                catch (Exception error) when (
                    error is IOException or
                    UnauthorizedAccessException or
                    System.Text.Json.JsonException or
                    InvalidDataException)
                {
                    lastError = error;
                    if (attempt < 2)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(15), cancellationToken);
                    }
                }
            }
            throw new IOException(
                "无法读取桌面在线会话所需的生产 Store 快照。",
                lastError);
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public async ValueTask UpdateAsync(
        Func<BridgeStoreSnapshot, BridgeStoreSnapshot> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            var current = CurrentSnapshot;
            await RequireOwnerLeaseAsync(cancellationToken);
            if (current.Store is null ||
                current.State is not BridgeProductionStoreState.Open)
            {
                throw new InvalidOperationException("生产 Store 尚未成功打开。");
            }
            var store = update(current.Store!) ??
                throw new InvalidOperationException(
                    "生产 Store 更新函数不能返回 null。");
            store = BridgeStoreRetention.PruneRoutes(store, DateTimeOffset.UtcNow);
            if (ReferenceEquals(store, current.Store))
            {
                return;
            }
            try
            {
                // Once the owner lease and update have been accepted, the durable
                // commit must finish even if the originating HTTP request disconnects.
                // Otherwise a cancelled request can leave all files committed while
                // permanently marking the in-process Store owner as failed.
                await WriteStoreWithRetryAsync(store, CancellationToken.None);
                await RequireOwnerLeaseAsync(CancellationToken.None);
            }
            catch (Exception error)
            {
                Volatile.Write(
                    ref snapshot,
                    current with
                    {
                        State = BridgeProductionStoreState.Failed,
                        StoreFiles = ExistingStoreFiles(options.DataDirectory),
                        FailureDetail = Failure("update", error),
                    });
                throw;
            }
            string? auditFailure = null;
            if (approvalAudit is not null)
            {
                try
                {
                    await approvalAudit.AppendChangesAsync(
                        current.Store!.Approvals,
                        store.Approvals,
                        CancellationToken.None);
                }
                catch (Exception error)
                {
                    auditFailure = $"approval-audit-failed:{error.GetType().Name}";
                }
            }
            Volatile.Write(
                ref snapshot,
                current with
                {
                    State = BridgeProductionStoreState.Open,
                    Store = store,
                    StoreFiles = ExistingStoreFiles(options.DataDirectory),
                    FailureDetail = null,
                    AuditFailureDetail = auditFailure,
                });
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        await lifecycleLock.WaitAsync(CancellationToken.None);
        try
        {
            var current = CurrentSnapshot;
            if (current.State is BridgeProductionStoreState.Closed)
            {
                return;
            }
            if (current.State is BridgeProductionStoreState.Open)
            {
                await RequireOwnerLeaseAsync(CancellationToken.None);
                await WriteStoreWithRetryAsync(current.Store!, CancellationToken.None);
                await RequireOwnerLeaseAsync(CancellationToken.None);
            }
            Volatile.Write(
                ref snapshot,
                new BridgeProductionStoreSnapshot(
                    BridgeProductionStoreState.Closed,
                    null,
                    ExistingStoreFiles(options.DataDirectory)));
        }
        finally
        {
            lifecycleLock.Release();
        }
    }


    private static BridgeProductionStoreSnapshot Redact(
        BridgeProductionStoreSnapshot current) =>
        current with { Store = null };
    private BridgeProductionStoreSnapshot RequireOpenStore()
    {
        var current = CurrentSnapshot;
        if (current.State is not BridgeProductionStoreState.Open || current.Store is null)
        {
            throw new InvalidOperationException("生产 Store 尚未成功打开。");
        }
        return current;
    }

    private async ValueTask RequireOwnerLeaseAsync(CancellationToken cancellationToken)
    {
        var heldLease = ownerLease.HeldLease;
        if (!ownerLease.IsHeld || heldLease is null)
        {
            throw new InvalidOperationException(
                "生产 Store 只能在 C# Active Owner 租约持有期间访问。");
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var observed = await inspectOwnerLease(cancellationToken);
            if (observed.State is ActiveOwnerLeaseState.Live &&
                observed.Record?.LeaseId == heldLease.LeaseId &&
                observed.Record.HostKind is "dotnet" &&
                observed.Record.InstanceName == options.InstanceName &&
                observed.Record.ProcessId == heldLease.ProcessId)
            {
                return;
            }
            if (attempt < 2 &&
                observed.State is ActiveOwnerLeaseState.Invalid or
                    ActiveOwnerLeaseState.Missing)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(15), cancellationToken);
                continue;
            }
            break;
        }
        throw new InvalidOperationException(
            "共享 Active Owner 租约身份已变化，拒绝访问生产 Store。");
    }

    private static string Failure(string operation, Exception error) =>
        $"store-{operation}-failed:{error.GetType().Name}";

    private async Task<BridgeStoreSnapshot> LoadStoreWithRetryAsync(
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await repository.LoadAsync(cancellationToken);
            }
            catch (BridgeStoreCorruptionException)
            {
                throw;
            }
            catch (Exception error) when (
                error is IOException or
                UnauthorizedAccessException or
                System.Text.Json.JsonException or
                InvalidDataException)
            {
                lastError = error;
                if (attempt < 2)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(25 * (attempt + 1)),
                        cancellationToken);
                }
            }
        }
        throw new IOException("生产 Store 读取重试后仍然失败。", lastError);
    }

    private async Task WriteStoreWithRetryAsync(
        BridgeStoreSnapshot store,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await repository.WriteAsync(store, cancellationToken);
                return;
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException)
            {
                lastError = error;
                if (attempt < 2)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(25 * (attempt + 1)),
                        cancellationToken);
                }
            }
        }
        throw new IOException("生产 Store 写入重试后仍然失败。", lastError);
    }

    private static BridgeJsonStoreRepository CreateRepository(BridgeHostOptions options)
    {
        EnsureActive(options);
        return new(
            options.DataDirectory,
            BridgeStoreAccess.ReadWriteActiveOwner);
    }

    private static void EnsureActive(BridgeHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException(
                "生产 Store Owner 只能用于 Active Host。");
        }
    }

    private static int ExistingStoreFiles(string dataDirectory) =>
        BridgeStoreFile.All.Count(file =>
            File.Exists(Path.Combine(dataDirectory, file.FileName)));

    private BridgeStoreSnapshot Bootstrap(BridgeStoreSnapshot store)
    {
        var bindings = store.Bindings;
        var controlToken = store.ControlToken;
        var settings = store.Settings;
        var changed = false;

        if (string.IsNullOrWhiteSpace(bindings.OwnerOpenId))
        {
            if (string.IsNullOrWhiteSpace(bindings.PairingCode))
            {
                bindings = CopyBindings(bindings);
                bindings.PairingCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(5));
                changed = true;
            }
        }
        else if (!string.IsNullOrWhiteSpace(bindings.PairingCode))
        {
            bindings = CopyBindings(bindings);
            bindings.PairingCode = null;
            changed = true;
        }

        if (!IsControlToken(controlToken.Token))
        {
            controlToken = new ControlTokenStoreDocument
            {
                Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
                ExtensionData = controlToken.ExtensionData,
            };
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.WorkspaceRoot))
        {
            settings = new SettingsStoreDocument
            {
                WorkspaceRoot = DefaultWorkspaceRoot(),
                NotifyActivity = settings.NotifyActivity,
                NotifyUserPrompts = settings.NotifyUserPrompts,
                AutoRetryErrors = settings.AutoRetryErrors,
                RetryMaxAttempts = settings.RetryMaxAttempts,
                RetryIntervalSeconds = settings.RetryIntervalSeconds,
                RetryJitterSeconds = settings.RetryJitterSeconds,
                AutoApprove = settings.AutoApprove,
                NotifyAutoApprovals = settings.NotifyAutoApprovals,
                ExtensionData = settings.ExtensionData,
            };
            changed = true;
        }

        return changed
            ? store with
            {
                Bindings = bindings,
                Settings = settings,
                ControlToken = controlToken,
            }
            : store;
    }

    private static BindingStoreDocument CopyBindings(BindingStoreDocument source) => new()
    {
        Users = new Dictionary<string, BindingStoreRecord>(
            source.Users,
            StringComparer.Ordinal),
        OwnerOpenId = source.OwnerOpenId,
        PairingCode = source.PairingCode,
        ExtensionData = source.ExtensionData,
    };

    private string DefaultWorkspaceRoot()
    {
        var configured = BridgeLocalConfiguration.Read(options, "DEFAULT_WORKSPACE_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }
        var bridgeRoot = BridgeLocalConfiguration.BridgeRoot(options);
        return Directory.GetParent(bridgeRoot)?.FullName ?? bridgeRoot;
    }

    private static bool IsControlToken(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
