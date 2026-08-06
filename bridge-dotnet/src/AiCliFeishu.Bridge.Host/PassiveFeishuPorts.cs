using System.Runtime.CompilerServices;
using AiCliFeishu.Bridge.Adapters.Feishu;

namespace AiCliFeishu.Bridge.Host;

/// <summary>
/// Passive Host 的飞书端口实现。它们让完整 Feishu Adapter 对象图进入装配根，
/// 但不会建立长连接、调用飞书 HTTP API 或读写本地文件。
/// </summary>
public sealed class PassiveFeishuEventSource : IFeishuEventSource
{
    public async IAsyncEnumerable<FeishuInboundEnvelope> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }
}

public sealed class PassiveFeishuGateway : IFeishuGateway
{
    public Task<string> SendTextAsync(
        string chatId,
        string text,
        CancellationToken cancellationToken = default) => Unavailable<string>();

    public Task<string> ReplyTextAsync(
        string messageId,
        string text,
        CancellationToken cancellationToken = default) => Unavailable<string>();

    public Task<string> SendCardAsync(
        string chatId,
        FeishuCardView card,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default) => Unavailable<string>();

    public Task PatchCardAsync(
        string messageId,
        FeishuCardView card,
        CancellationToken cancellationToken = default) => Unavailable();

    public Task<FeishuSessionGroup> CreateSessionGroupAsync(
        string ownerOpenId,
        string name,
        string description,
        CancellationToken cancellationToken = default) => Unavailable<FeishuSessionGroup>();

    public Task UpdateSessionGroupNameAsync(
        string chatId,
        string name,
        CancellationToken cancellationToken = default) => Unavailable();

    public Task DeleteSessionGroupAsync(
        string chatId,
        CancellationToken cancellationToken = default) => Unavailable();

    public Task<long> DownloadMessageResourceAsync(
        string messageId,
        string fileKey,
        string resourceType,
        string destinationPath,
        long maxBytes,
        CancellationToken cancellationToken = default) => Unavailable<long>();

    public Task<string> SendLocalFileAsync(
        string chatId,
        string filePath,
        CancellationToken cancellationToken = default) => Unavailable<string>();

    private static Task Unavailable() => Task.FromException(Error());

    private static Task<T> Unavailable<T>() => Task.FromException<T>(Error());

    private static InvalidOperationException Error() =>
        new("Passive Host 不连接飞书，也不发送或更新飞书消息。");
}

public interface IBridgeFeishuAdapterAssembly
{
    BridgeFeishuAdapterSnapshot Validate();

    BridgeFeishuAdapterSnapshot Snapshot();
}

public sealed record BridgeFeishuAdapterSnapshot(
    string Mode,
    IReadOnlyList<string> Components,
    bool LiveEventStreamEnabled,
    bool OutboundMessagingEnabled);

/// <summary>
/// Feishu Adapter 的组合根描述。构造成功即证明标准意图入口、事件规范化、
/// 卡片渲染、交互协调和传输端口的依赖链完整；Validate 不启动事件泵。
/// </summary>
public sealed class BridgeFeishuAdapterAssembly(
    IFeishuEventSource eventSource,
    IFeishuIntentSink intentSink,
    IFeishuGateway gateway,
    IFeishuCardRenderer cardRenderer,
    FeishuEventNormalizer eventNormalizer,
    FeishuInteractionCoordinator interactionCoordinator,
    FeishuEventPump eventPump,
    BridgeHostOptions options) : IBridgeFeishuAdapterAssembly
{
    private static readonly string[] componentNames =
    [
        "card-renderer",
        "event-normalizer",
        "event-pump",
        "event-source",
        "gateway",
        "intent-sink",
        "interaction-coordinator",
    ];

    public BridgeFeishuAdapterSnapshot Validate()
    {
        _ = intentSink;
        _ = cardRenderer;
        _ = eventNormalizer;
        _ = interactionCoordinator;
        _ = eventPump;
        if (options.OwnershipMode is BridgeOwnershipMode.Passive &&
            (eventSource is not PassiveFeishuEventSource ||
             gateway is not PassiveFeishuGateway))
        {
            throw new InvalidOperationException(
                "Passive Host 的 Feishu Adapter 必须使用无网络事件源和拒绝发送的 Gateway。");
        }
        return Snapshot();
    }

    public BridgeFeishuAdapterSnapshot Snapshot()
    {
        var active = options.OwnershipMode is BridgeOwnershipMode.Active;
        return new(
            active ? "active" : "passive",
            componentNames,
            active && eventSource is not PassiveFeishuEventSource,
            active && gateway is not PassiveFeishuGateway);
    }
}
