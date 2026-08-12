using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.OpenCode;

namespace AiCliFeishu.Bridge.Host;

/// <summary>
/// Keeps one generation-bound SSE subscription for each registered OpenCode
/// endpoint. Passive mode still validates an empty directory without doing I/O.
/// </summary>
public sealed class BridgeOpenCodeEventSubsystem :
    IBridgeHostSubsystem,
    IBridgeHostSubsystemHealth,
    IBridgeBackgroundSubsystem
{
    private static readonly TimeSpan DefaultRetryBase = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultRetryMaximum = TimeSpan.FromSeconds(30);
    private readonly object sync = new();
    private readonly IOpenCodeEndpointDirectory endpoints;
    private readonly OpenCodeRuntimeEventPump eventPump;
    private readonly IOpenCodeEventSource eventSource;
    private readonly BridgeHostOptions options;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly TimeSpan retryBase;
    private readonly TimeSpan retryMaximum;
    private readonly Dictionary<int, Subscription> subscriptions = [];
    private CancellationTokenSource? shutdown;
    private Task? supervisor;
    private bool started;
    private int readySubscriptions;
    private int reconnectingSubscriptions;

    public BridgeOpenCodeEventSubsystem(
        IOpenCodeEndpointDirectory endpoints,
        OpenCodeRuntimeEventPump eventPump,
        IOpenCodeEventSource eventSource,
        BridgeHostOptions options)
        : this(
            endpoints,
            eventPump,
            eventSource,
            options,
            static (duration, cancellationToken) =>
                Task.Delay(duration, cancellationToken),
            DefaultRetryBase,
            DefaultRetryMaximum)
    {
    }

    internal BridgeOpenCodeEventSubsystem(
        IOpenCodeEndpointDirectory endpoints,
        OpenCodeRuntimeEventPump eventPump,
        IOpenCodeEventSource eventSource,
        BridgeHostOptions options,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeSpan retryBase,
        TimeSpan retryMaximum)
    {
        this.endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        this.eventPump = eventPump ?? throw new ArgumentNullException(nameof(eventPump));
        this.eventSource = eventSource ??
            throw new ArgumentNullException(nameof(eventSource));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
        this.retryBase = retryBase > TimeSpan.Zero
            ? retryBase
            : throw new ArgumentOutOfRangeException(nameof(retryBase));
        this.retryMaximum = retryMaximum >= retryBase
            ? retryMaximum
            : throw new ArgumentOutOfRangeException(nameof(retryMaximum));
    }

    public string Name => "opencode-event-pump";

    public Task? Completion
    {
        get
        {
            lock (sync)
            {
                return options.OwnershipMode is BridgeOwnershipMode.Active
                    ? supervisor
                    : null;
            }
        }
    }

    public BridgeComponentHealth ComponentHealth
    {
        get
        {
            lock (sync)
            {
                if (!started)
                {
                    return new(Name, "starting");
                }
                if (options.OwnershipMode is BridgeOwnershipMode.Passive)
                {
                    return new(Name, "passive", "event-endpoints-disabled");
                }
                return new(
                    Name,
                    "healthy",
                    $"subscriptions={subscriptions.Count};ready={readySubscriptions};reconnecting={reconnectingSubscriptions}");
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.OwnershipMode is BridgeOwnershipMode.Passive)
        {
            return StartPassiveAsync(cancellationToken);
        }
        if (endpoints is not IBridgeOpenCodeEndpointRegistrationDirectory directory ||
            eventSource is not IBridgeOpenCodeEventStreamOwner source)
        {
            throw new InvalidOperationException(
                "Active Host 的 OpenCode 事件流必须使用生产目录和事件源 Owner。");
        }
        lock (sync)
        {
            if (started)
            {
                throw new InvalidOperationException("OpenCode 事件子系统已经启动。");
            }
            shutdown = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            started = true;
            supervisor = SuperviseAsync(directory, source, shutdown.Token);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? cancellation;
        Task? running;
        lock (sync)
        {
            if (!started)
            {
                return;
            }
            cancellation = shutdown;
            running = supervisor;
            started = false;
            shutdown = null;
            supervisor = null;
        }
        cancellation?.Cancel();
        try
        {
            if (running is not null)
            {
                await running.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            cancellation?.Dispose();
            lock (sync)
            {
                readySubscriptions = 0;
                reconnectingSubscriptions = 0;
            }
        }
    }

    private async Task StartPassiveAsync(CancellationToken cancellationToken)
    {
        var readyEndpoints = endpoints.ListReady();
        if (readyEndpoints.Count != 0)
        {
            throw new InvalidOperationException(
                "Passive Host cannot subscribe to OpenCode event endpoints.");
        }
        await Task.WhenAll(readyEndpoints.Select(endpoint =>
            eventPump.RunAsync(endpoint, cancellationToken)));
        lock (sync)
        {
            started = true;
        }
    }

    private async Task SuperviseAsync(
        IBridgeOpenCodeEndpointRegistrationDirectory directory,
        IBridgeOpenCodeEventStreamOwner source,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var observedRevision = directory.Snapshot.Revision;
                await ReconcileAsync(
                    directory,
                    source,
                    directory.ListRegistrations(),
                    cancellationToken);
                await directory.WaitForChangeAsync(
                    observedRevision,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await CancelAllAsync();
        }
    }

    private async Task ReconcileAsync(
        IBridgeOpenCodeEndpointRegistrationDirectory directory,
        IBridgeOpenCodeEventStreamOwner source,
        IReadOnlyList<BridgeOpenCodeEndpointIdentity> registrations,
        CancellationToken cancellationToken)
    {
        var current = registrations.ToDictionary(
            registration => registration.Port);
        var retired = new List<Subscription>();
        lock (sync)
        {
            foreach (var subscription in subscriptions.Values.ToArray())
            {
                if (!current.TryGetValue(subscription.Identity.Port, out var identity) ||
                    identity.Generation != subscription.Identity.Generation)
                {
                    subscriptions.Remove(subscription.Identity.Port);
                    subscription.Cancellation.Cancel();
                    retired.Add(subscription);
                }
            }
            UpdateCountsLocked();
        }
        await AwaitSubscriptionsAsync(retired);

        lock (sync)
        {
            foreach (var identity in registrations)
            {
                if (subscriptions.ContainsKey(identity.Port))
                {
                    continue;
                }
                var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                var subscription = new Subscription(identity, linked);
                subscriptions.Add(identity.Port, subscription);
                subscription.Task = RunSubscriptionAsync(
                    directory,
                    source,
                    subscription,
                    linked.Token);
            }
            UpdateCountsLocked();
        }
    }

    private async Task RunSubscriptionAsync(
        IBridgeOpenCodeEndpointRegistrationDirectory directory,
        IBridgeOpenCodeEventStreamOwner source,
        Subscription subscription,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SetSubscriptionState(subscription, ready: false, reconnecting: attempt > 0);
                bool healthy;
                try
                {
                    healthy = await source.ProbeHealthAsync(
                        subscription.Identity.Endpoint,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    healthy = false;
                }
                if (!healthy)
                {
                    directory.SetReady(
                        subscription.Identity.Port,
                        subscription.Identity.Generation,
                        ready: false);
                    await DelayForRetryAsync(attempt, cancellationToken);
                    attempt = Math.Min(attempt + 1, 20);
                    continue;
                }
                if (!directory.SetReady(
                        subscription.Identity.Port,
                        subscription.Identity.Generation,
                        ready: true))
                {
                    return;
                }
                SetSubscriptionState(subscription, ready: true, reconnecting: false);
                var observedEvent = false;
                try
                {
                    await eventPump.RunAsync(
                        subscription.Identity.Endpoint with { Ready = true },
                        (rawEvent, token) =>
                        {
                            observedEvent = true;
                            TrackSession(directory, subscription.Identity, rawEvent);
                            token.ThrowIfCancellationRequested();
                            return ValueTask.CompletedTask;
                        },
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                }
                directory.SetReady(
                    subscription.Identity.Port,
                    subscription.Identity.Generation,
                    ready: false);
                SetSubscriptionState(subscription, ready: false, reconnecting: true);
                await DelayForRetryAsync(
                    observedEvent ? 0 : attempt,
                    cancellationToken);
                attempt = observedEvent ? 0 : Math.Min(attempt + 1, 20);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            directory.SetReady(
                subscription.Identity.Port,
                subscription.Identity.Generation,
                ready: false);
            SetSubscriptionState(subscription, ready: false, reconnecting: false);
        }
    }

    private async Task DelayForRetryAsync(
        int attempt,
        CancellationToken cancellationToken)
    {
        var exponent = Math.Min(Math.Max(0, attempt), 20);
        var milliseconds = Math.Min(
            retryBase.TotalMilliseconds * Math.Pow(2, exponent),
            retryMaximum.TotalMilliseconds);
        await delay(TimeSpan.FromMilliseconds(milliseconds), cancellationToken);
    }

    private static void TrackSession(
        IBridgeOpenCodeEndpointRegistrationDirectory directory,
        BridgeOpenCodeEndpointIdentity identity,
        OpenCodeRawEvent rawEvent)
    {
        var sessionId = SessionId(rawEvent);
        if (!ValidSessionId(sessionId))
        {
            return;
        }
        if (rawEvent.Type == "session.deleted")
        {
            directory.ForgetSession(identity.Port, identity.Generation, sessionId!);
            return;
        }
        if (TracksSession(rawEvent.Type))
        {
            directory.RememberObservedSession(
                identity.Port,
                identity.Generation,
                sessionId!);
        }
    }

    private static string? SessionId(OpenCodeRawEvent rawEvent)
    {
        var properties = ObjectProperty(rawEvent.Properties, "data") ??
            rawEvent.Properties;
        var direct = OptionalString(properties, "sessionID") ??
            OptionalString(properties, "sessionId") ??
            OptionalString(properties, "session_id");
        if (direct is not null)
        {
            return direct;
        }
        foreach (var name in new[] { "info", "message", "part" })
        {
            if (ObjectProperty(properties, name) is not { } nested)
            {
                continue;
            }
            var nestedSession = OptionalString(nested, "sessionID") ??
                OptionalString(nested, "sessionId") ??
                OptionalString(nested, "session_id");
            if (nestedSession is not null)
            {
                return nestedSession;
            }
            if (name == "info" &&
                rawEvent.Type.StartsWith("session.", StringComparison.Ordinal))
            {
                return OptionalString(nested, "id");
            }
        }
        return rawEvent.Type is "session.created" or "session.updated"
            ? OptionalString(properties, "id")
            : null;
    }

    private static bool TracksSession(string eventType) => eventType is
        "session.created" or
        "session.updated" or
        "session.idle" or
        "session.error" or
        "session.compacted" or
        "session.status" or
        "message.updated" or
        "message.part.updated" or
        "permission.asked" or
        "permission.v2.asked" or
        "permission.updated" or
        "permission.replied" or
        "permission.v2.replied" or
        "question.asked" or
        "question.replied" or
        "question.rejected";

    private static JsonElement? ObjectProperty(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Object
            ? property
            : null;

    private static string? OptionalString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool ValidSessionId(string? sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) &&
        sessionId.Length <= 512 &&
        !sessionId.Any(char.IsControl);

    private void SetSubscriptionState(
        Subscription subscription,
        bool ready,
        bool reconnecting)
    {
        lock (sync)
        {
            subscription.Ready = ready;
            subscription.Reconnecting = reconnecting;
            UpdateCountsLocked();
        }
    }

    private void UpdateCountsLocked()
    {
        readySubscriptions = subscriptions.Values.Count(value => value.Ready);
        reconnectingSubscriptions = subscriptions.Values.Count(
            value => value.Reconnecting);
    }

    private async Task CancelAllAsync()
    {
        Subscription[] current;
        lock (sync)
        {
            current = subscriptions.Values.ToArray();
            subscriptions.Clear();
            foreach (var subscription in current)
            {
                subscription.Cancellation.Cancel();
            }
            UpdateCountsLocked();
        }
        await AwaitSubscriptionsAsync(current);
    }

    private static async Task AwaitSubscriptionsAsync(
        IEnumerable<Subscription> subscriptions)
    {
        foreach (var subscription in subscriptions)
        {
            try
            {
                if (subscription.Task is not null)
                {
                    await subscription.Task;
                }
            }
            finally
            {
                subscription.Cancellation.Dispose();
            }
        }
    }

    private sealed class Subscription(
        BridgeOpenCodeEndpointIdentity identity,
        CancellationTokenSource cancellation)
    {
        public BridgeOpenCodeEndpointIdentity Identity { get; } = identity;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task? Task { get; set; }
        public bool Ready { get; set; }
        public bool Reconnecting { get; set; }
    }
}
