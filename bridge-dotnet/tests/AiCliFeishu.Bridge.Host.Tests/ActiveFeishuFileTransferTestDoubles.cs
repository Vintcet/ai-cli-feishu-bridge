using AiCliFeishu.Bridge.Adapters.Feishu;

namespace AiCliFeishu.Bridge.Host.Tests;

internal sealed class RecordingFileTransferCoordinator :
    IBridgeActiveFileTransferCoordinator
{
    private readonly Dictionary<string, List<BridgeSavedAttachment>> staged =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PendingRequest>> requests =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> queueDepth =
        new(StringComparer.Ordinal);

    public List<(string Key, string MessageId, int Count)> Downloads { get; } = [];

    public List<(string SessionId, string ChatId, bool FileReturn, bool Queued)> Dispatches { get; } = [];

    public List<(string SessionId, string ChatId, IReadOnlyList<string> Paths)> SentFiles { get; } = [];

    public string AttachmentKey(string openId, string chatId) => $"{openId}\0{chatId}";

    public Task<IReadOnlyList<BridgeSavedAttachment>> DownloadAndStageAsync(
        string key,
        string messageId,
        IReadOnlyList<FeishuAttachment> attachments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Downloads.Add((key, messageId, attachments.Count));
        var saved = attachments.Select((attachment, index) => new BridgeSavedAttachment(
            $"K:\\uploads\\{messageId}-{index + 1}-{attachment.Name ?? "attachment.bin"}",
            attachment.Name ?? "attachment.bin",
            1)).ToArray();
        var current = staged.GetValueOrDefault(key) ?? [];
        staged[key] = current.Concat(saved).ToList();
        return Task.FromResult<IReadOnlyList<BridgeSavedAttachment>>(saved);
    }

    public IReadOnlyList<BridgeSavedAttachment> PeekAttachments(string key) =>
        staged.GetValueOrDefault(key)?.ToArray() ?? [];

    public IReadOnlyList<BridgeSavedAttachment> TakeAttachments(string key)
    {
        if (!staged.Remove(key, out var files))
        {
            return [];
        }
        return files.ToArray();
    }

    public void ObservePromptDispatch(
        string sessionId,
        string chatId,
        bool requestFileReturn,
        bool queued)
    {
        Dispatches.Add((sessionId, chatId, requestFileReturn, queued));
        var depth = queueDepth.GetValueOrDefault(sessionId);
        if (queued)
        {
            depth++;
            queueDepth[sessionId] = depth;
        }
        if (requestFileReturn)
        {
            var list = requests.GetValueOrDefault(sessionId) ?? [];
            list.Add(new(chatId, queued ? depth : 0));
            requests[sessionId] = list;
        }
    }

    public BridgeFileReturnRequest? AdvanceReturnRequest(string sessionId)
    {
        var list = requests.GetValueOrDefault(sessionId);
        PendingRequest? eligible = null;
        if (list is not null)
        {
            var index = list.FindIndex(item => item.RemainingStops == 0);
            if (index >= 0)
            {
                eligible = list[index];
                list.RemoveAt(index);
            }
            foreach (var item in list)
            {
                if (item.RemainingStops > 0)
                {
                    item.RemainingStops--;
                }
            }
            if (list.Count == 0)
            {
                requests.Remove(sessionId);
            }
        }
        var depth = queueDepth.GetValueOrDefault(sessionId);
        if (depth <= 1) queueDepth.Remove(sessionId);
        else queueDepth[sessionId] = depth - 1;
        return eligible is null ? null : new(eligible.ChatId);
    }

    public void RemoveSession(string sessionId)
    {
        requests.Remove(sessionId);
        queueDepth.Remove(sessionId);
    }

    public Task<BridgeFileReturnResult> SendRequestedFilesAsync(
        string sessionId,
        string chatId,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SentFiles.Add((sessionId, chatId, candidates.ToArray()));
        return Task.FromResult(new BridgeFileReturnResult(candidates.Count, 0));
    }

    private sealed class PendingRequest(string chatId, int remainingStops)
    {
        public string ChatId { get; } = chatId;

        public int RemainingStops { get; set; } = remainingStops;
    }
}
