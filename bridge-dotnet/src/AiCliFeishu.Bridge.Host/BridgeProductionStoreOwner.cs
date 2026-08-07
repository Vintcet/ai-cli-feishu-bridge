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
    NodeStoreSnapshot? Store,
    int StoreFiles);

internal interface IBridgeProductionStoreOwner
{
    BridgeProductionStoreSnapshot Snapshot { get; }

    ValueTask OpenAsync(CancellationToken cancellationToken = default);

    ValueTask FlushAsync(CancellationToken cancellationToken = default);

    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}

internal sealed class ActiveProductionStoreOwner :
    IBridgeProductionStoreOwner,
    IBridgeHostSubsystem,
    IBridgeHostSubsystemHealth
{
    private readonly BridgeHostOptions options;
    private readonly Func<CancellationToken, ValueTask<ActiveOwnerLeaseSnapshot>> inspectOwnerLease;
    private readonly IBridgeActiveOwnerLeaseLifecycle ownerLease;
    private readonly SemaphoreSlim lifecycleLock = new(1, 1);
    private readonly NodeJsonStoreRepository repository;
    private BridgeProductionStoreSnapshot snapshot = new(
        BridgeProductionStoreState.NotOpened,
        null,
        0);

    public ActiveProductionStoreOwner(
        BridgeHostOptions options,
        IBridgeActiveOwnerLeaseLifecycle ownerLease)
        : this(
            options,
            ownerLease,
            CreateRepository(options),
            new ActiveOwnerLeaseObserver(options.DataDirectory).InspectAsync)
    {
    }

    internal ActiveProductionStoreOwner(
        BridgeHostOptions options,
        IBridgeActiveOwnerLeaseLifecycle ownerLease,
        NodeJsonStoreRepository repository,
        Func<CancellationToken, ValueTask<ActiveOwnerLeaseSnapshot>>? inspectOwnerLease = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ownerLease);
        ArgumentNullException.ThrowIfNull(repository);
        EnsureActive(options);
        if (repository.Access is not NodeStoreAccess.ReadWriteActiveOwner)
        {
            throw new InvalidOperationException(
                "生产 Store Owner 必须使用 Active Owner 专用写入 Repository。");
        }
        this.options = options;
        this.ownerLease = ownerLease;
        this.repository = repository;
        this.inspectOwnerLease = inspectOwnerLease ??
            new ActiveOwnerLeaseObserver(options.DataDirectory).InspectAsync;
    }

    public string Name => "production-store";

    public BridgeProductionStoreSnapshot Snapshot => Redact(Volatile.Read(ref snapshot));

    internal BridgeProductionStoreSnapshot CurrentSnapshot => Volatile.Read(ref snapshot);

    public BridgeComponentHealth ComponentHealth
    {
        get
        {
            var current = CurrentSnapshot;
            return current.State switch
            {
                BridgeProductionStoreState.Open => new(
                    Name,
                    "ready",
                    $"loaded files={current.StoreFiles}"),
                BridgeProductionStoreState.Failed => new(Name, "failed", "store-open-failed"),
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
                var store = await repository.LoadAsync(cancellationToken);
                await RequireOwnerLeaseAsync(cancellationToken);
                Volatile.Write(
                    ref snapshot,
                    new BridgeProductionStoreSnapshot(
                        BridgeProductionStoreState.Open,
                        store,
                        ExistingStoreFiles(options.DataDirectory)));
            }
            catch
            {
                Volatile.Write(
                    ref snapshot,
                    new BridgeProductionStoreSnapshot(
                        BridgeProductionStoreState.Failed,
                        null,
                        0));
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
            await repository.WriteAsync(current.Store!, cancellationToken);
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
                await repository.WriteAsync(current.Store!, CancellationToken.None);
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
        var observed = await inspectOwnerLease(cancellationToken);
        if (observed.State is not ActiveOwnerLeaseState.Live ||
            observed.Record?.LeaseId != heldLease.LeaseId ||
            observed.Record.HostKind is not "dotnet" ||
            observed.Record.InstanceName != options.InstanceName ||
            observed.Record.ProcessId != heldLease.ProcessId)
        {
            throw new InvalidOperationException(
                "共享 Active Owner 租约身份已变化，拒绝访问生产 Store。");
        }
    }

    private static NodeJsonStoreRepository CreateRepository(BridgeHostOptions options)
    {
        EnsureActive(options);
        return new(
            options.DataDirectory,
            NodeStoreAccess.ReadWriteActiveOwner);
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
        NodeStoreFile.All.Count(file =>
            File.Exists(Path.Combine(dataDirectory, file.FileName)));
}
