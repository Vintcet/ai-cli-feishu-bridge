using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class PersistentFeishuInboundDeduplicatorTests
{
    [TestMethod]
    public void PersistsClaimsAndAllowsRetryOnlyAfterRelease()
    {
        var store = new RecordingStoreOwner(EmptyStore());
        var clock = new FixedTimeProvider(
            DateTimeOffset.Parse("2026-08-10T00:00:00Z"));
        var deduplicator = new PersistentFeishuInboundDeduplicator(store, clock);

        Assert.IsTrue(deduplicator.TryClaim("event-1"));
        Assert.IsFalse(deduplicator.TryClaim("event-1"));
        Assert.AreEqual(
            "2026-08-10T00:00:00.0000000+00:00",
            store.Current.Routes.ProcessedInbound["event-1"]);

        deduplicator.Release("event-1");

        Assert.IsFalse(store.Current.Routes.ProcessedInbound.ContainsKey("event-1"));
        Assert.IsTrue(deduplicator.TryClaim("event-1"));
    }

    private static BridgeStoreSnapshot EmptyStore() => new(
        new BindingStoreDocument(),
        new SessionStoreDocument(),
        new RouteStoreDocument(),
        new ApprovalStoreDocument(),
        new SettingsStoreDocument(),
        new ControlTokenStoreDocument());

    private sealed class RecordingStoreOwner(BridgeStoreSnapshot current) :
        IBridgeProductionStoreOwner
    {
        private readonly object sync = new();
        private BridgeStoreSnapshot current = current;

        public BridgeStoreSnapshot Current
        {
            get
            {
                lock (sync)
                {
                    return current;
                }
            }
        }

        public BridgeProductionStoreSnapshot Snapshot => new(
            BridgeProductionStoreState.Open,
            Current,
            6);

        public ValueTask OpenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<BridgeStoreSnapshot> ReadAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Current);

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask UpdateAsync(
            Func<BridgeStoreSnapshot, BridgeStoreSnapshot> update,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                current = update(current);
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
