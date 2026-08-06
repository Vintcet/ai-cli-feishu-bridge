using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishuControl;

internal sealed class ProductionBridgeStoreHandoffInspector :
    IBridgeStoreHandoffInspector
{
    private readonly Func<CancellationToken, ValueTask<ActiveOwnerLeaseSnapshot>>
        inspectLease;
    private readonly Func<CancellationToken, Task> validateStore;

    public ProductionBridgeStoreHandoffInspector(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException(
                "生产 Store 目录不能为空。",
                nameof(dataDirectory));
        }

        var fullDataDirectory = Path.GetFullPath(dataDirectory);
        var observer = new ActiveOwnerLeaseObserver(fullDataDirectory);
        var store = new NodeJsonStoreRepository(
            fullDataDirectory,
            NodeStoreAccess.ReadOnly);
        inspectLease = observer.InspectAsync;
        validateStore = async cancellationToken =>
        {
            _ = await store.LoadAsync(cancellationToken);
        };
    }

    internal ProductionBridgeStoreHandoffInspector(
        Func<CancellationToken, ValueTask<ActiveOwnerLeaseSnapshot>> inspectLease,
        Func<CancellationToken, Task> validateStore)
    {
        this.inspectLease = inspectLease ??
            throw new ArgumentNullException(nameof(inspectLease));
        this.validateStore = validateStore ??
            throw new ArgumentNullException(nameof(validateStore));
    }

    public async ValueTask<BridgeStoreHandoffEvidence> InspectAsync(
        CancellationToken cancellationToken)
    {
        var before = await inspectLease(cancellationToken);
        if (before.State is ActiveOwnerLeaseState.Live or ActiveOwnerLeaseState.Invalid)
        {
            return Evidence(
                storeFlushed: false,
                storeCompatible: true,
                before.State);
        }

        var storeCompatible = true;
        try
        {
            await validateStore(cancellationToken);
        }
        catch (Exception error) when (IsStoreCompatibilityFailure(error))
        {
            storeCompatible = false;
        }

        var after = await inspectLease(cancellationToken);
        if (!LeaseIsStable(before, after))
        {
            return new(
                StoreFlushed: false,
                StoreCompatible: storeCompatible,
                BridgeCutoverLeaseState.Invalid);
        }

        return Evidence(
            storeFlushed: after.State is ActiveOwnerLeaseState.Missing,
            storeCompatible,
            after.State);
    }

    private static bool LeaseIsStable(
        ActiveOwnerLeaseSnapshot before,
        ActiveOwnerLeaseSnapshot after)
    {
        if (before.State != after.State)
        {
            return false;
        }
        if (before.State is ActiveOwnerLeaseState.Missing)
        {
            return before.Record is null && after.Record is null;
        }
        return before.Record is not null && before.Record == after.Record;
    }

    private static bool IsStoreCompatibilityFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or JsonException;

    private static BridgeStoreHandoffEvidence Evidence(
        bool storeFlushed,
        bool storeCompatible,
        ActiveOwnerLeaseState state) =>
        new(
            storeFlushed,
            storeCompatible,
            state switch
            {
                ActiveOwnerLeaseState.Missing => BridgeCutoverLeaseState.Missing,
                ActiveOwnerLeaseState.Stale => BridgeCutoverLeaseState.Stale,
                ActiveOwnerLeaseState.Live => BridgeCutoverLeaseState.Live,
                ActiveOwnerLeaseState.Invalid => BridgeCutoverLeaseState.Invalid,
                _ => BridgeCutoverLeaseState.Invalid,
            });
}
