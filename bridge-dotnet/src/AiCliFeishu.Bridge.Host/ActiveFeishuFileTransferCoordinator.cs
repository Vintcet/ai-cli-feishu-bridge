using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishu.Bridge.Host;

internal sealed record BridgeSavedAttachment(
    string AbsolutePath,
    string FileName,
    long Size);

internal sealed record BridgeFileDirectiveResult(
    string DisplayMessage,
    IReadOnlyList<string> Paths);

internal sealed record BridgeFileReturnRequest(string ChatId);

internal sealed record BridgeFileReturnResult(int SentCount, int FailedCount);

internal interface IBridgeActiveFileTransferCoordinator
{
    string AttachmentKey(string openId, string chatId);

    Task<IReadOnlyList<BridgeSavedAttachment>> DownloadAndStageAsync(
        string key,
        string messageId,
        IReadOnlyList<FeishuAttachment> attachments,
        CancellationToken cancellationToken = default);

    IReadOnlyList<BridgeSavedAttachment> PeekAttachments(string key);

    IReadOnlyList<BridgeSavedAttachment> TakeAttachments(string key);

    void ObservePromptDispatch(
        string sessionId,
        string chatId,
        bool requestFileReturn,
        bool queued);

    BridgeFileReturnRequest? AdvanceReturnRequest(string sessionId);

    void RemoveSession(string sessionId);

    Task<BridgeFileReturnResult> SendRequestedFilesAsync(
        string sessionId,
        string chatId,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken = default);
}

internal sealed partial class ActiveFeishuFileTransferCoordinator :
    IBridgeActiveFileTransferCoordinator,
    IDisposable
{
    private static readonly TimeSpan ReturnRequestTtl = TimeSpan.FromHours(2);
    private readonly object sync = new();
    private readonly BridgeHostOptions options;
    private readonly IBridgeProductionStoreOwner storeOwner;
    private readonly IFeishuGateway gateway;
    private readonly TimeProvider clock;
    private readonly Lazy<ActiveFeishuFileTransferSettings> settings;
    private readonly Lazy<LocalAttachmentStore> attachmentStore;
    private readonly Dictionary<string, StagedAttachments> stagedAttachments =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PendingFileReturnRequest>> returnRequests =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> managedQueueDepth =
        new(StringComparer.Ordinal);
    private bool disposed;

    public ActiveFeishuFileTransferCoordinator(
        BridgeHostOptions options,
        IBridgeProductionStoreOwner storeOwner,
        IFeishuGateway gateway)
        : this(
            options,
            storeOwner,
            gateway,
            () => ActiveFeishuFileTransferSettings.Load(options),
            TimeProvider.System)
    {
    }

    internal ActiveFeishuFileTransferCoordinator(
        BridgeHostOptions options,
        IBridgeProductionStoreOwner storeOwner,
        IFeishuGateway gateway,
        ActiveFeishuFileTransferSettings settings,
        TimeProvider? timeProvider = null)
        : this(
            options,
            storeOwner,
            gateway,
            () => settings,
            timeProvider ?? TimeProvider.System)
    {
        ArgumentNullException.ThrowIfNull(settings);
    }

    private ActiveFeishuFileTransferCoordinator(
        BridgeHostOptions options,
        IBridgeProductionStoreOwner storeOwner,
        IFeishuGateway gateway,
        Func<ActiveFeishuFileTransferSettings> loadSettings,
        TimeProvider clock)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.storeOwner = storeOwner ?? throw new ArgumentNullException(nameof(storeOwner));
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        settings = new(
            () => (loadSettings ?? throw new ArgumentNullException(nameof(loadSettings)))(),
            LazyThreadSafetyMode.ExecutionAndPublication);
        attachmentStore = new(
            () => new LocalAttachmentStore(this.gateway, this.settings.Value, this.clock),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private void PruneStagedLocked(DateTimeOffset now)
    {
            var cutoff = SafeCutoff(now, settings.Value.UploadTtl);
        foreach (var key in stagedAttachments
            .Where(item => item.Value.CreatedAt < cutoff)
            .Select(item => item.Key)
            .ToArray())
        {
            stagedAttachments.Remove(key);
        }
    }

    private void PruneReturnRequestsLocked(DateTimeOffset now)
    {
        foreach (var (sessionId, requests) in returnRequests.ToArray())
        {
            requests.RemoveAll(request => request.ExpiresAt <= now);
            if (requests.Count == 0)
            {
                returnRequests.Remove(sessionId);
            }
        }
    }

    private void EnsureAvailable()
    {
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException(
                "飞书附件与文件回传协调器只能用于 Active Host。");
        }
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed), this);
    }

    private void EnsureAvailableLocked() =>
        ObjectDisposedException.ThrowIf(disposed, this);

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength] + "…";

    private static DateTimeOffset SafeCutoff(
        DateTimeOffset now,
        TimeSpan ttl) =>
        ttl >= now - DateTimeOffset.MinValue
            ? DateTimeOffset.MinValue
            : now - ttl;

    private sealed record StagedAttachments(
        DateTimeOffset CreatedAt,
        IReadOnlyList<BridgeSavedAttachment> Files);

    private sealed class PendingFileReturnRequest(
        string chatId,
        int remainingStops,
        DateTimeOffset expiresAt)
    {
        public string ChatId { get; } = chatId;

        public int RemainingStops { get; set; } = remainingStops;

        public DateTimeOffset ExpiresAt { get; } = expiresAt;
    }
}
