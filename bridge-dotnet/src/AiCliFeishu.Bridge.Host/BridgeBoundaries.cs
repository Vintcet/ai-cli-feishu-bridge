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

public sealed class BridgeRuntimeEventIngress(
    IEnumerable<IBridgeRuntimeEventHandler> handlers,
    int completedEventCapacity = 4_096) : IRuntimeEventSink, IDisposable
{
    private readonly IBridgeRuntimeEventHandler[] eventHandlers = handlers.ToArray();
    private readonly int capacity = Math.Max(1, completedEventCapacity);
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly LinkedList<string> completedOrder = [];
    private readonly HashSet<string> completed = new(StringComparer.Ordinal);

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
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var eventKey = $"{runtimeEvent.Runtime}\n{runtimeEvent.EventId}";
            if (completed.Contains(eventKey))
            {
                return;
            }

            await handler.HandleAsync(runtimeEvent, cancellationToken);
            completed.Add(eventKey);
            completedOrder.AddLast(eventKey);
            while (completedOrder.Count > capacity)
            {
                var oldest = completedOrder.First!;
                completedOrder.RemoveFirst();
                completed.Remove(oldest.Value);
            }
        }
        finally
        {
            writeGate.Release();
        }
    }

    public void ValidateConfiguration() => _ = OptionalSingleHandler();

    public void Dispose() => writeGate.Dispose();

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

    public void ValidateConfiguration() => _ = OptionalSingleHandler();

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
    int RuntimeEventHandlers,
    int FeishuIntentHandlers,
    bool Passive);

public sealed class BridgeBoundaryCatalog(
    IEnumerable<IRuntimeAdapter> adapters,
    BridgeRuntimeEventIngress runtimeIngress,
    BridgeFeishuIntentIngress feishuIngress,
    BridgeHostOptions options)
{
    private readonly IRuntimeAdapter[] registeredAdapters = adapters.ToArray();

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
        return new(
            registeredAdapters
                .Select(adapter => adapter.Runtime)
                .OrderBy(runtime => runtime, StringComparer.Ordinal)
                .ToArray(),
            runtimeIngress.HandlerCount,
            feishuIngress.HandlerCount,
            options.OwnershipMode is BridgeOwnershipMode.Passive);
    }
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
