using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishu.Bridge.Host;

public static class BridgeStoreShadowStatuses
{
    public const string NotLoaded = "not_loaded";
    public const string Loaded = "loaded";
    public const string Missing = "missing";
    public const string Incompatible = "incompatible";
}

public sealed record BridgeStoreShadowSnapshot(
    string Status,
    NodeStoreCoreState? Core,
    int StoreFiles,
    int Bindings,
    string? IncompatibleFile = null)
{
    public static BridgeStoreShadowSnapshot NotLoaded { get; } = new(
        BridgeStoreShadowStatuses.NotLoaded,
        null,
        0,
        0);
}

public interface IBridgeStoreShadow
{
    BridgeStoreShadowSnapshot Snapshot { get; }

    BridgeComponentHealth ComponentHealth { get; }

    Task RefreshAsync(CancellationToken cancellationToken = default);
}

public sealed class ReadOnlyNodeStoreShadow(BridgeHostOptions options)
    : IBridgeStoreShadow, IBridgeHostSubsystem, IBridgeHostSubsystemHealth
{
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private BridgeStoreShadowSnapshot snapshot = BridgeStoreShadowSnapshot.NotLoaded;

    public string Name => "node-store-shadow";

    public BridgeStoreShadowSnapshot Snapshot => Volatile.Read(ref snapshot);

    public BridgeComponentHealth ComponentHealth
    {
        get
        {
            var current = Snapshot;
            return current.Status switch
            {
                BridgeStoreShadowStatuses.Loaded => new(
                    Name,
                    "ready",
                    $"loaded files={current.StoreFiles} sessions={current.Core!.Sessions.Sessions.Count} " +
                    $"routes={current.Core.Routes.Messages.Count} " +
                    $"approvals={current.Core.Approvals.Requests.Count} bindings={current.Bindings}"),
                BridgeStoreShadowStatuses.Missing => new(Name, "ready", "missing"),
                BridgeStoreShadowStatuses.Incompatible => new(
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
            var existingFiles = NodeStoreFile.All.Count(file =>
                File.Exists(Path.Combine(options.DataDirectory, file.FileName)));
            if (existingFiles == 0)
            {
                Volatile.Write(
                    ref snapshot,
                    new BridgeStoreShadowSnapshot(
                        BridgeStoreShadowStatuses.Missing,
                        NodeStoreCoreProjection.Project(await new NodeJsonStoreRepository(
                            options.DataDirectory,
                            NodeStoreAccess.ReadOnly).LoadAsync(cancellationToken)),
                        0,
                        0));
                return;
            }

            try
            {
                var store = await new NodeJsonStoreRepository(
                    options.DataDirectory,
                    NodeStoreAccess.ReadOnly).LoadAsync(cancellationToken);
                Volatile.Write(
                    ref snapshot,
                    new BridgeStoreShadowSnapshot(
                        BridgeStoreShadowStatuses.Loaded,
                        NodeStoreCoreProjection.Project(store),
                        existingFiles,
                        store.Bindings.Users.Count));
            }
            catch (NodeStoreValidationException error)
            {
                Volatile.Write(
                    ref snapshot,
                    new BridgeStoreShadowSnapshot(
                        BridgeStoreShadowStatuses.Incompatible,
                        null,
                        existingFiles,
                        0,
                        error.FileName));
            }
            catch (System.Text.Json.JsonException)
            {
                Volatile.Write(
                    ref snapshot,
                    new BridgeStoreShadowSnapshot(
                        BridgeStoreShadowStatuses.Incompatible,
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
