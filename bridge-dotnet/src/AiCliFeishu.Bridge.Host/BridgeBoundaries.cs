using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

public interface IBridgeRuntimeEventHandler
{
    Task HandleAsync(
        RuntimeEventEnvelope runtimeEvent,
        CancellationToken cancellationToken = default);
}

public interface IBridgeFeishuIntentHandler
{
    Task<FeishuCallbackResult?> HandleAsync(
        FeishuIntent intent,
        CancellationToken cancellationToken = default);
}

public interface IBridgeRuntimeCommandGateway
{
    bool IsReady(string runtime, RuntimeSession session);

    Task DispatchAsync(
        RuntimeCommandEnvelope command,
        CancellationToken cancellationToken = default);
}

public sealed class BridgeRuntimeCommandUnavailableException : InvalidOperationException
{
    public BridgeRuntimeCommandUnavailableException(string message)
        : base(message)
    {
    }

    public BridgeRuntimeCommandUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class BridgeRuntimeCommandGateway(RuntimeCommandDispatcher dispatcher)
    : IBridgeRuntimeCommandGateway
{
    public bool IsReady(string runtime, RuntimeSession session) =>
        dispatcher.IsReady(runtime, session);

    public Task DispatchAsync(
        RuntimeCommandEnvelope command,
        CancellationToken cancellationToken = default) =>
        dispatcher.DispatchAsync(command, cancellationToken);
}

public sealed class BridgeRuntimeCommandIngress : IBridgeRuntimeCommandGateway, IDisposable
{
    private readonly BridgeRuntimeCommandGateway gateway;
    private readonly BridgeHostOptions options;
    private readonly int capacity;
    private readonly SemaphoreSlim dispatchGate = new(1, 1);
    private readonly LinkedList<string> completedOrder = [];
    private readonly HashSet<string> completed = new(StringComparer.Ordinal);

    public BridgeRuntimeCommandIngress(
        BridgeRuntimeCommandGateway gateway,
        BridgeHostOptions options,
        int completedCommandCapacity = 4_096)
    {
        this.gateway = gateway;
        this.options = options;
        capacity = Math.Max(1, completedCommandCapacity);
    }

    public bool IsReady(string runtime, RuntimeSession session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtime);
        ArgumentNullException.ThrowIfNull(session);
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            return false;
        }
        try
        {
            return gateway.IsReady(runtime, session);
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    public async Task DispatchAsync(
        RuntimeCommandEnvelope command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var validation = BridgeProtocolValidator.Validate(command);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(
                $"运行时命令不符合 Bridge Protocol：{string.Join("；", validation.Errors)}");
        }
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new BridgeRuntimeCommandUnavailableException(
                "C# Bridge Host 当前是只读模式，不允许执行 Runtime 命令。");
        }

        await dispatchGate.WaitAsync(cancellationToken);
        try
        {
            var commandKey = $"{command.Runtime}\n{command.CommandId}";
            if (completed.Contains(commandKey))
            {
                return;
            }

            if (RequiresReadySession(command.CommandType))
            {
                var session = new RuntimeSession(
                    command.Session!.ExternalId,
                    command.Session.Cwd);
                bool ready;
                try
                {
                    ready = gateway.IsReady(command.Runtime, session);
                }
                catch (KeyNotFoundException error)
                {
                    throw new BridgeRuntimeCommandUnavailableException(
                        $"运行时 {command.Runtime} 尚未注册 Adapter。",
                        error);
                }
                if (!ready)
                {
                    throw new BridgeRuntimeCommandUnavailableException(
                        $"运行时 {command.Runtime} 的目标会话尚未就绪。");
                }
            }

            try
            {
                await gateway.DispatchAsync(command, cancellationToken);
            }
            catch (Exception error) when (error is KeyNotFoundException or NotSupportedException)
            {
                throw new BridgeRuntimeCommandUnavailableException(
                    $"运行时 {command.Runtime} 当前无法执行 {command.CommandType}。",
                    error);
            }

            completed.Add(commandKey);
            completedOrder.AddLast(commandKey);
            while (completedOrder.Count > capacity)
            {
                var oldest = completedOrder.First!;
                completedOrder.RemoveFirst();
                completed.Remove(oldest.Value);
            }
        }
        finally
        {
            dispatchGate.Release();
        }
    }

    public void Dispose() => dispatchGate.Dispose();

    private static bool RequiresReadySession(string commandType) => commandType is
        RuntimeCommandTypes.PromptSend or
        RuntimeCommandTypes.ApprovalResolve or
        RuntimeCommandTypes.InputResolve;
}

public sealed class BridgeRuntimeEventIngress(
    IEnumerable<IBridgeRuntimeEventHandler> handlers,
    int completedEventCapacity = 4_096) : IRuntimeEventSink, IDisposable
{
    private readonly IBridgeRuntimeEventHandler[] eventHandlers = handlers.ToArray();
    private readonly int capacity = Math.Max(1, completedEventCapacity);
    private readonly object streamGateLock = new();
    private readonly Dictionary<string, StreamGate> streamGates =
        new(StringComparer.Ordinal);
    private readonly object completedLock = new();
    private readonly LinkedList<string> completedOrder = [];
    private readonly HashSet<string> completed = new(StringComparer.Ordinal);
    private bool disposed;

    public int HandlerCount => eventHandlers.Length;

    public async Task PublishAsync(
        RuntimeEventEnvelope runtimeEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        var validation = BridgeProtocolValidator.Validate(runtimeEvent);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(
                $"运行时事件不符合 Bridge Protocol：{string.Join("；", validation.Errors)}");
        }
        var handler = RequireSingleHandler();
        var streamKey = $"{runtimeEvent.Runtime}\n{runtimeEvent.Session!.ExternalId}";
        var streamGate = AcquireStreamGate(streamKey);
        try
        {
            await streamGate.Gate.WaitAsync(cancellationToken);
            try
            {
                var eventKey = $"{streamKey}\n{runtimeEvent.EventId}";
                lock (completedLock)
                {
                    if (completed.Contains(eventKey))
                    {
                        return;
                    }
                }

                await handler.HandleAsync(runtimeEvent, cancellationToken);
                lock (completedLock)
                {
                    completed.Add(eventKey);
                    completedOrder.AddLast(eventKey);
                    while (completedOrder.Count > capacity)
                    {
                        var oldest = completedOrder.First!;
                        completedOrder.RemoveFirst();
                        completed.Remove(oldest.Value);
                    }
                }
            }
            finally
            {
                streamGate.Gate.Release();
            }
        }
        finally
        {
            ReleaseStreamGate(streamKey, streamGate);
        }
    }

    public void ValidateConfiguration() => _ = RequireSingleHandler();

    public void Dispose()
    {
        StreamGate[] gates;
        lock (streamGateLock)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            gates = [];
            foreach (var entry in streamGates.ToArray())
            {
                entry.Value.DisposeWhenIdle = true;
                if (entry.Value.Users == 0)
                {
                    streamGates.Remove(entry.Key);
                    gates = [.. gates, entry.Value];
                }
            }
        }
        foreach (var gate in gates)
        {
            gate.Gate.Dispose();
        }
    }

    private StreamGate AcquireStreamGate(string streamKey)
    {
        lock (streamGateLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!streamGates.TryGetValue(streamKey, out var streamGate))
            {
                streamGate = new();
                streamGates.Add(streamKey, streamGate);
            }
            streamGate.Users++;
            return streamGate;
        }
    }

    private void ReleaseStreamGate(string streamKey, StreamGate streamGate)
    {
        SemaphoreSlim? gateToDispose = null;
        lock (streamGateLock)
        {
            streamGate.Users--;
            if (streamGate.Users == 0 &&
                streamGate.DisposeWhenIdle &&
                streamGates.TryGetValue(streamKey, out var current) &&
                ReferenceEquals(current, streamGate))
            {
                streamGates.Remove(streamKey);
                gateToDispose = streamGate.Gate;
            }
        }
        gateToDispose?.Dispose();
    }

    private sealed class StreamGate
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public int Users { get; set; }

        public bool DisposeWhenIdle { get; set; }
    }

    private IBridgeRuntimeEventHandler RequireSingleHandler() =>
        OptionalSingleHandler() ?? throw new InvalidOperationException(
            "Bridge Host 尚未注册 Runtime 事件业务处理器。");

    private IBridgeRuntimeEventHandler? OptionalSingleHandler() => eventHandlers.Length switch
    {
        0 => null,
        1 => eventHandlers[0],
        _ => throw new InvalidOperationException(
            "Bridge Host 只能注册一个 Runtime 事件业务处理器，以保证单一状态所有者。"),
    };
}

public sealed class BridgeFeishuIntentIngress(
    IEnumerable<IBridgeFeishuIntentHandler> handlers) : IFeishuIntentSink
{
    private readonly IBridgeFeishuIntentHandler[] intentHandlers = handlers.ToArray();

    public int HandlerCount => intentHandlers.Length;

    public Task<FeishuCallbackResult?> PublishAsync(
        FeishuIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ValidateIntent(intent);
        var handler = RequireSingleHandler();
        return handler.HandleAsync(intent, cancellationToken);
    }

    public void ValidateConfiguration() => _ = RequireSingleHandler();

    private IBridgeFeishuIntentHandler RequireSingleHandler() =>
        OptionalSingleHandler() ?? throw new InvalidOperationException(
            "Bridge Host 尚未注册飞书标准意图业务处理器。");

    private IBridgeFeishuIntentHandler? OptionalSingleHandler() => intentHandlers.Length switch
    {
        0 => null,
        1 => intentHandlers[0],
        _ => throw new InvalidOperationException(
            "Bridge Host 只能注册一个飞书标准意图业务处理器，以保证单一业务决策方。"),
    };

    private static void ValidateIntent(FeishuIntent intent)
    {
        if (string.IsNullOrWhiteSpace(intent.EventId) ||
            string.IsNullOrWhiteSpace(intent.IntentType) ||
            string.IsNullOrWhiteSpace(intent.OperatorOpenId) ||
            string.IsNullOrWhiteSpace(intent.ChatId) ||
            string.IsNullOrWhiteSpace(intent.MessageId) ||
            string.IsNullOrWhiteSpace(intent.TraceId))
        {
            throw new InvalidDataException("飞书标准意图缺少事件、操作人、会话、消息或 traceId。");
        }
        if (!FeishuIntentTypes.All.Contains(intent.IntentType))
        {
            throw new InvalidDataException($"不支持的飞书标准意图 {intent.IntentType}。");
        }
    }
}

public sealed record BridgeBoundarySnapshot(
    IReadOnlyList<string> RegisteredRuntimes,
    IReadOnlyList<BridgeRuntimeAdapterSnapshot> RuntimeAdapters,
    BridgeRuntimeIngressSnapshot RuntimeIngress,
    BridgeFeishuAdapterSnapshot FeishuAdapter,
    int RuntimeEventHandlers,
    int FeishuIntentHandlers,
    bool Passive);

public sealed record BridgeRuntimeAdapterSnapshot(
    string Runtime,
    IReadOnlyList<RuntimeCapability> Capabilities);

public sealed class BridgeBoundaryCatalog(
    IEnumerable<IRuntimeAdapter> adapters,
    BridgeRuntimeEventIngress runtimeIngress,
    BridgeFeishuIntentIngress feishuIngress,
    BridgeHostOptions options,
    IEnumerable<IBridgeFeishuAdapterAssembly>? feishuAdapters = null,
    IEnumerable<IBridgeRuntimeIngressAssembly>? runtimeIngressAdapters = null)
{
    private readonly IRuntimeAdapter[] registeredAdapters = adapters.ToArray();
    private readonly IBridgeFeishuAdapterAssembly[] registeredFeishuAdapters =
        feishuAdapters?.ToArray() ?? [];
    private readonly IBridgeRuntimeIngressAssembly[] registeredRuntimeIngressAdapters =
        runtimeIngressAdapters?.ToArray() ?? [];

    public RuntimeAdapterRegistry BuildRuntimeRegistry()
    {
        var registry = new RuntimeAdapterRegistry();
        foreach (var adapter in registeredAdapters)
        {
            registry.Register(adapter);
        }
        return registry;
    }

    public BridgeBoundarySnapshot Validate()
    {
        runtimeIngress.ValidateConfiguration();
        feishuIngress.ValidateConfiguration();
        _ = BuildRuntimeRegistry();
        _ = RequireSingleRuntimeIngressAdapter().Validate();
        _ = RequireSingleFeishuAdapter().Validate();
        return Snapshot();
    }

    public BridgeBoundarySnapshot Snapshot()
    {
        var runtimeAdapters = registeredAdapters
            .OrderBy(adapter => adapter.Runtime, StringComparer.Ordinal)
            .Select(adapter => new BridgeRuntimeAdapterSnapshot(
                adapter.Runtime,
                adapter.Capabilities.OrderBy(capability => capability).ToArray()))
            .ToArray();
        return new(
            runtimeAdapters.Select(adapter => adapter.Runtime).ToArray(),
            runtimeAdapters,
            RequireSingleRuntimeIngressAdapter().Snapshot(),
            RequireSingleFeishuAdapter().Snapshot(),
            runtimeIngress.HandlerCount,
            feishuIngress.HandlerCount,
            options.OwnershipMode is BridgeOwnershipMode.Passive);
    }

    private IBridgeFeishuAdapterAssembly RequireSingleFeishuAdapter() =>
        registeredFeishuAdapters.Length switch
        {
            1 => registeredFeishuAdapters[0],
            0 => throw new InvalidOperationException(
                "Bridge Host 尚未注册 Feishu Adapter 装配边界。"),
            _ => throw new InvalidOperationException(
                "Bridge Host 只能注册一个 Feishu Adapter，以保证飞书事件和发送所有权唯一。"),
        };

    private IBridgeRuntimeIngressAssembly RequireSingleRuntimeIngressAdapter() =>
        registeredRuntimeIngressAdapters.Length switch
        {
            1 => registeredRuntimeIngressAdapters[0],
            0 => throw new InvalidOperationException(
                "Bridge Host 尚未注册 Runtime 入站 Adapter 装配边界。"),
            _ => throw new InvalidOperationException(
                "Bridge Host 只能注册一个 Runtime 入站 Adapter 所有者。"),
        };
}

public sealed class BridgeBoundarySubsystem(BridgeBoundaryCatalog catalog) : IBridgeHostSubsystem
{
    public string Name => "standard-boundaries";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = catalog.Validate();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
