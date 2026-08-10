using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishu.Bridge.Host;

internal sealed partial class LocalAttachmentStore : IDisposable
{
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);
    private static readonly Regex InvalidFileNameCharacters = new(
        "[<>:\"/\\\\|?*\\u0000-\\u001f]",
        RegexOptions.CultureInvariant);
    private static readonly Regex InvalidTokenCharacters = new(
        "[^a-zA-Z0-9_-]",
        RegexOptions.CultureInvariant);
    private readonly IFeishuGateway gateway;
    private readonly ActiveFeishuFileTransferSettings settings;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private DateTimeOffset lastPrunedAt = DateTimeOffset.MinValue;
    private bool disposed;

    public LocalAttachmentStore(
        IFeishuGateway gateway,
        ActiveFeishuFileTransferSettings settings,
        TimeProvider clock)
    {
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (settings.InboundFileMaxBytes <= 0 ||
            settings.InboundAttachmentMaxCount <= 0 ||
            settings.UploadMaxFiles <= 0 ||
            settings.UploadMaxBytes <= 0 ||
            settings.UploadTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(settings));
        }
    }

    public async Task<IReadOnlyList<BridgeSavedAttachment>> DownloadAsync(
        string messageId,
        IReadOnlyList<FeishuAttachment> attachments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(attachments);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed), this);
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (attachments.Count == 0)
            {
                return [];
            }
            if (attachments.Count > settings.InboundAttachmentMaxCount)
            {
                throw new InvalidDataException(
                    $"每条消息最多接收 {settings.InboundAttachmentMaxCount} 个附件。");
            }
            EnsureSafeDirectoryRoot(settings.UploadsDirectory);
            await PruneIfNeededAsync();
            var usage = MeasureDirectory(settings.UploadsDirectory);
            if ((long)usage.FileCount + attachments.Count > settings.UploadMaxFiles)
            {
                throw new InvalidDataException(
                    $"附件暂存区最多保留 {settings.UploadMaxFiles} 个文件，" +
                    "请等待旧附件自动清理后重试。");
            }

            var directory = Path.Combine(
                Path.GetFullPath(settings.UploadsDirectory),
                clock.GetUtcNow().ToString("yyyy-MM", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);
            EnsureNotReparseDirectory(directory);
            var saved = new List<BridgeSavedAttachment>(attachments.Count);
            string? currentDestination = null;
            try
            {
                for (var index = 0; index < attachments.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attachment = ValidateAttachment(attachments[index]);
                    var safeName = SanitizeFileName(
                        attachment.Name ?? DefaultFileName(attachment.Kind));
                    currentDestination = Path.Combine(
                        directory,
                        $"{SanitizeToken(messageId)}-{index + 1}-" +
                        $"{Guid.NewGuid():N}"[..8] + $"-{safeName}");
                    var size = await gateway.DownloadMessageResourceAsync(
                        messageId,
                        attachment.Key,
                        attachment.Kind,
                        currentDestination,
                        settings.InboundFileMaxBytes,
                        cancellationToken);
                    if (size <= 0 || size > settings.InboundFileMaxBytes)
                    {
                        throw new InvalidDataException(
                            $"飞书附件为空或超过本机限制（{settings.InboundFileMaxBytes} bytes）。");
                    }
                    var downloaded = new FileInfo(currentDestination);
                    if (!downloaded.Exists ||
                        (downloaded.Attributes & (FileAttributes.Directory |
                            FileAttributes.ReparsePoint)) != 0 ||
                        downloaded.Length != size ||
                        downloaded.Length > settings.InboundFileMaxBytes)
                    {
                        throw new InvalidDataException("飞书附件下载大小校验失败。");
                    }
                    var savedAttachment = new BridgeSavedAttachment(
                        Path.GetFullPath(currentDestination),
                        safeName,
                        size);
                    saved.Add(savedAttachment);
                    currentDestination = null;
                    usage.FileCount++;
                    usage.TotalBytes = checked(usage.TotalBytes + size);
                    if (usage.TotalBytes > settings.UploadMaxBytes)
                    {
                        throw new InvalidDataException(
                            $"附件暂存区总容量不能超过 " +
                            $"{FormatBytes(settings.UploadMaxBytes)}，" +
                            "请等待旧附件自动清理后重试。");
                    }
                }
                return saved;
            }
            catch
            {
                TryDelete(currentDestination);
                foreach (var item in saved)
                {
                    TryDelete(item.AbsolutePath);
                }
                throw;
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            operationGate.Dispose();
        }
    }

    private Task PruneIfNeededAsync()
    {
        var now = clock.GetUtcNow();
        if (now - lastPrunedAt < PruneInterval)
        {
            return Task.CompletedTask;
        }
        lastPrunedAt = now;
        try
        {
            PruneDirectory(
                settings.UploadsDirectory,
                SafeCutoff(now, settings.UploadTtl));
        }
        catch
        {
            // Cleanup is best effort. Quota measurement below remains fail closed.
        }
        return Task.CompletedTask;
    }

    private static FeishuAttachment ValidateAttachment(FeishuAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (attachment.Kind is not ("image" or "file") ||
            string.IsNullOrWhiteSpace(attachment.Key))
        {
            throw new InvalidDataException("飞书附件元数据不完整。");
        }
        return attachment;
    }

    private static string SanitizeFileName(string value)
    {
        var normalized = value.Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');
        var baseName = (separator >= 0 ? normalized[(separator + 1)..] : normalized)
            .Normalize(NormalizationForm.FormC);
        var cleaned = InvalidFileNameCharacters.Replace(baseName, "_");
        if (cleaned.Length > 120)
        {
            cleaned = cleaned[..120];
        }
        cleaned = cleaned.TrimEnd('.', ' ').Trim();
        return cleaned.Length == 0 ? "attachment.bin" : cleaned;
    }

    private static string SanitizeToken(string value)
    {
        var cleaned = InvalidTokenCharacters.Replace(value, string.Empty);
        if (cleaned.Length > 48)
        {
            cleaned = cleaned[^48..];
        }
        return cleaned.Length == 0 ? "message" : cleaned;
    }

    private static string DefaultFileName(string kind) =>
        kind == "image" ? "feishu-image.jpg" : "feishu-file.bin";

    private static DirectoryUsage MeasureDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return new();
        }
        EnsureNotReparseDirectory(directory);
        var usage = new DirectoryUsage();
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }
            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                var nested = MeasureDirectory(entry.FullName);
                usage.FileCount = checked(usage.FileCount + nested.FileCount);
                usage.TotalBytes = checked(usage.TotalBytes + nested.TotalBytes);
            }
            else
            {
                usage.FileCount++;
                usage.TotalBytes = checked(usage.TotalBytes + ((FileInfo)entry).Length);
            }
        }
        return usage;
    }

    private static void PruneDirectory(string directory, DateTimeOffset cutoff)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }
        EnsureNotReparseDirectory(directory);
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }
            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                PruneDirectory(entry.FullName, cutoff);
                if (!Directory.EnumerateFileSystemEntries(entry.FullName).Any())
                {
                    Directory.Delete(entry.FullName);
                }
            }
            else if (entry.LastWriteTimeUtc < cutoff.UtcDateTime)
            {
                File.Delete(entry.FullName);
            }
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

}
