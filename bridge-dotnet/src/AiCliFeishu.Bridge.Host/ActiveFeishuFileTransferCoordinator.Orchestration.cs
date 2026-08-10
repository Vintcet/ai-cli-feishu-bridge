using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishu.Bridge.Host;

internal sealed partial class ActiveFeishuFileTransferCoordinator
{
    public string AttachmentKey(string openId, string chatId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(openId);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        EnsureAvailable();
        return $"{openId}\0{chatId}";
    }

    public async Task<IReadOnlyList<BridgeSavedAttachment>> DownloadAndStageAsync(
        string key,
        string messageId,
        IReadOnlyList<FeishuAttachment> attachments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(attachments);
        EnsureAvailable();
        var saved = await attachmentStore.Value.DownloadAsync(
            messageId,
            attachments,
            cancellationToken);
        if (saved.Count == 0)
        {
            return saved;
        }

        lock (sync)
        {
            EnsureAvailableLocked();
            var now = clock.GetUtcNow();
            PruneStagedLocked(now);
            var current = stagedAttachments.GetValueOrDefault(key)?.Files ?? [];
            var limit = Math.Max(1, settings.Value.InboundAttachmentMaxCount * 2);
            stagedAttachments[key] = new(
                now,
                current.Concat(saved).TakeLast(limit).ToArray());
        }
        return saved;
    }

    public IReadOnlyList<BridgeSavedAttachment> PeekAttachments(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        EnsureAvailable();
        lock (sync)
        {
            EnsureAvailableLocked();
            PruneStagedLocked(clock.GetUtcNow());
            return stagedAttachments.TryGetValue(key, out var staged)
                ? staged.Files.ToArray()
                : [];
        }
    }

    public IReadOnlyList<BridgeSavedAttachment> TakeAttachments(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        EnsureAvailable();
        lock (sync)
        {
            EnsureAvailableLocked();
            PruneStagedLocked(clock.GetUtcNow());
            if (!stagedAttachments.Remove(key, out var staged))
            {
                return [];
            }
            return staged.Files.ToArray();
        }
    }

    public void ObservePromptDispatch(
        string sessionId,
        string chatId,
        bool requestFileReturn,
        bool queued)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        EnsureAvailable();
        lock (sync)
        {
            EnsureAvailableLocked();
            var now = clock.GetUtcNow();
            PruneReturnRequestsLocked(now);
            var depth = managedQueueDepth.GetValueOrDefault(sessionId);
            if (queued)
            {
                depth = checked(depth + 1);
                managedQueueDepth[sessionId] = depth;
            }
            if (!requestFileReturn)
            {
                return;
            }
            var requests = returnRequests.GetValueOrDefault(sessionId) ?? [];
            requests.Add(new(chatId, queued ? depth : 0, now + ReturnRequestTtl));
            returnRequests[sessionId] = requests;
        }
    }

    public BridgeFileReturnRequest? AdvanceReturnRequest(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        EnsureAvailable();
        lock (sync)
        {
            EnsureAvailableLocked();
            var now = clock.GetUtcNow();
            PruneReturnRequestsLocked(now);
            var requests = returnRequests.GetValueOrDefault(sessionId);
            PendingFileReturnRequest? eligible = null;
            if (requests is not null)
            {
                var eligibleIndex = requests.FindIndex(request =>
                    request.RemainingStops == 0);
                if (eligibleIndex >= 0)
                {
                    eligible = requests[eligibleIndex];
                    requests.RemoveAt(eligibleIndex);
                }
                foreach (var request in requests)
                {
                    if (request.RemainingStops > 0)
                    {
                        request.RemainingStops--;
                    }
                }
                if (requests.Count == 0)
                {
                    returnRequests.Remove(sessionId);
                }
            }

            var depth = managedQueueDepth.GetValueOrDefault(sessionId);
            if (depth <= 1)
            {
                managedQueueDepth.Remove(sessionId);
            }
            else
            {
                managedQueueDepth[sessionId] = depth - 1;
            }
            return eligible is null ? null : new(eligible.ChatId);
        }
    }

    public void RemoveSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }
        EnsureAvailable();
        lock (sync)
        {
            EnsureAvailableLocked();
            returnRequests.Remove(sessionId);
            managedQueueDepth.Remove(sessionId);
        }
    }

    public async Task<BridgeFileReturnResult> SendRequestedFilesAsync(
        string sessionId,
        string chatId,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        ArgumentNullException.ThrowIfNull(candidates);
        EnsureAvailable();
        var store = await storeOwner.ReadAsync(cancellationToken);
        if (!store.Sessions.Sessions.TryGetValue(sessionId, out var session))
        {
            throw new InvalidOperationException("文件回传对应的会话不存在。");
        }

        var errors = new List<string>();
        var sentCount = 0;
        foreach (var candidate in candidates.Take(3))
        {
            try
            {
                var file = await BridgeFileTransferProtocol.ValidateFileAsync(
                    candidate,
                    session.Cwd,
                    settings.Value.OutboundFileMaxBytes,
                    cancellationToken);
                var messageId = await gateway.SendLocalFileAsync(
                    chatId,
                    file.Path,
                    cancellationToken);
                if (string.IsNullOrWhiteSpace(messageId))
                {
                    throw new InvalidDataException("飞书文件发送结果缺少消息 ID。");
                }
                await RememberFileRouteAsync(
                    messageId,
                    sessionId,
                    chatId,
                    cancellationToken);
                sentCount++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                errors.Add($"{candidate}：{error.Message}");
            }
        }

        if (errors.Count != 0)
        {
            await gateway.SendTextAsync(
                chatId,
                $"文件回传结果：成功 {sentCount} 个，失败 {errors.Count} 个。\n" +
                string.Join('\n', errors.Select(error => $"- {Truncate(error, 400)}")),
                cancellationToken);
        }
        return new(sentCount, errors.Count);
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            stagedAttachments.Clear();
            returnRequests.Clear();
            managedQueueDepth.Clear();
        }
        if (attachmentStore.IsValueCreated)
        {
            attachmentStore.Value.Dispose();
        }
    }
}
