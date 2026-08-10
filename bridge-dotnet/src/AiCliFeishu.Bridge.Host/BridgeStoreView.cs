using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishu.Bridge.Host;

public static class BridgeStoreViewStatuses
{
    public const string NotLoaded = "not_loaded";
    public const string Loaded = "loaded";
    public const string Missing = "missing";
    public const string Incompatible = "incompatible";
    public const string Failed = "failed";
}

public sealed record BridgeStoreViewSnapshot(
    string Status,
    BridgeStoreCoreState? Core,
    int StoreFiles,
    int Bindings,
    string? IncompatibleFile = null)
{
    public static BridgeStoreViewSnapshot NotLoaded { get; } = new(
        BridgeStoreViewStatuses.NotLoaded,
        null,
        0,
        0);
}

public interface IBridgeStoreView
{
    BridgeStoreViewSnapshot Snapshot { get; }

    BridgeComponentHealth ComponentHealth { get; }

    Task RefreshAsync(CancellationToken cancellationToken = default);
}

public sealed class ReadOnlyBridgeStoreView(BridgeHostOptions options)
    : IBridgeStoreView,
      IBridgeControlStoreStatusSource,
      IBridgeHostSubsystem,
      IBridgeHostSubsystemHealth
{
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private BridgeStoreViewSnapshot snapshot = BridgeStoreViewSnapshot.NotLoaded;

    public string Name => "bridge-store-readonly";

    public BridgeStoreViewSnapshot Snapshot => Volatile.Read(ref snapshot);

    BridgeControlStoreStatus IBridgeControlStoreStatusSource.Status =>
        BridgeControlStoreStatusProjection.FromStoreView(Snapshot);

    public BridgeComponentHealth ComponentHealth
    {
        get
        {
            var current = Snapshot;
            return current.Status switch
            {
                BridgeStoreViewStatuses.Loaded => new(
                    Name,
                    "ready",
                    $"loaded files={current.StoreFiles} sessions={current.Core!.Sessions.Sessions.Count} " +
                    $"routes={current.Core.Routes.Messages.Count} " +
                    $"approvals={current.Core.Approvals.Requests.Count} bindings={current.Bindings}"),
                BridgeStoreViewStatuses.Missing => new(Name, "ready", "missing"),
                BridgeStoreViewStatuses.Incompatible => new(
                    Name,
                    "failed",
                    $"incompatible file={current.IncompatibleFile ?? "unknown"}"),
                _ => new(Name, "starting"),
            };
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        RefreshAsync(cancellationToken);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            var existingFiles = BridgeStoreFile.All.Count(file =>
                File.Exists(Path.Combine(options.DataDirectory, file.FileName)));
            if (existingFiles == 0)
            {
                Volatile.Write(
                    ref snapshot,
                    new BridgeStoreViewSnapshot(
                        BridgeStoreViewStatuses.Missing,
                        BridgeStoreCoreProjection.Project(await new BridgeJsonStoreRepository(
                            options.DataDirectory,
                            BridgeStoreAccess.ReadOnly).LoadAsync(cancellationToken)),
                        0,
                        0));
                return;
            }

            try
            {
                var store = await new BridgeJsonStoreRepository(
                    options.DataDirectory,
                    BridgeStoreAccess.ReadOnly).LoadAsync(cancellationToken);
                Volatile.Write(
                    ref snapshot,
                    new BridgeStoreViewSnapshot(
                        BridgeStoreViewStatuses.Loaded,
                        BridgeStoreCoreProjection.Project(store),
                        existingFiles,
                        store.Bindings.Users.Count));
            }
            catch (BridgeStoreValidationException error)
            {
                Volatile.Write(
                    ref snapshot,
                    new BridgeStoreViewSnapshot(
                        BridgeStoreViewStatuses.Incompatible,
                        null,
                        existingFiles,
                        0,
                        error.FileName));
            }
            catch (System.Text.Json.JsonException)
            {
                Volatile.Write(
                    ref snapshot,
                    new BridgeStoreViewSnapshot(
                        BridgeStoreViewStatuses.Incompatible,
                        null,
                        existingFiles,
                        0,
                        "json"));
            }
        }
        finally
        {
            refreshLock.Release();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
