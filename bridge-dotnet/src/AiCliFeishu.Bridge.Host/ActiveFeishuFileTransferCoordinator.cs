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

internal sealed class ActiveFeishuFileTransferCoordinator :
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

    private async Task RememberFileRouteAsync(
        string messageId,
        string sessionId,
        string chatId,
        CancellationToken cancellationToken)
    {
        var route = new MessageRouteStoreRecord
        {
            MessageId = messageId,
            SessionId = sessionId,
            ChatId = chatId,
            Kind = BridgeRuntimeNotificationKinds.Stop,
            CreatedAt = clock.GetUtcNow().ToString("O"),
        };
        await storeOwner.UpdateAsync(
            store => AddRoute(store, route),
            cancellationToken);
    }

    private static NodeStoreSnapshot AddRoute(
        NodeStoreSnapshot store,
        MessageRouteStoreRecord route)
    {
        var messages = new Dictionary<string, MessageRouteStoreRecord>(
            store.Routes.Messages,
            StringComparer.Ordinal)
        {
            [route.MessageId] = route,
        };
        return store with
        {
            Routes = new()
            {
                Messages = messages,
                ProcessedInbound = new Dictionary<string, string>(
                    store.Routes.ProcessedInbound,
                    StringComparer.Ordinal),
                ExtensionData = CloneExtensions(store.Routes.ExtensionData),
            },
        };
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

    private static Dictionary<string, JsonElement>? CloneExtensions(
        Dictionary<string, JsonElement>? extensions) =>
        extensions?.ToDictionary(
            item => item.Key,
            item => item.Value.Clone(),
            StringComparer.Ordinal);

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

internal sealed record ActiveFeishuFileTransferSettings(
    string UploadsDirectory,
    long InboundFileMaxBytes,
    int InboundAttachmentMaxCount,
    int UploadMaxFiles,
    long UploadMaxBytes,
    TimeSpan UploadTtl,
    long OutboundFileMaxBytes)
{
    private const long DefaultInboundFileMaxBytes = 25 * 1024 * 1024;
    private const int DefaultInboundAttachmentMaxCount = 4;
    private const int DefaultUploadMaxFiles = 500;
    private const long DefaultUploadMaxBytes = 1024L * 1024 * 1024;
    private const long DefaultUploadTtlMilliseconds = 7L * 24 * 60 * 60 * 1000;
    private const long DefaultOutboundFileMaxBytes = 30 * 1024 * 1024;
    private static readonly string[] VariableNames =
    [
        "FEISHU_INBOUND_FILE_MAX_BYTES",
        "FEISHU_INBOUND_ATTACHMENT_MAX_COUNT",
        "FEISHU_UPLOAD_MAX_FILES",
        "FEISHU_UPLOAD_MAX_BYTES",
        "FEISHU_UPLOAD_TTL_MS",
        "FEISHU_OUTBOUND_FILE_MAX_BYTES",
    ];

    public static ActiveFeishuFileTransferSettings Load(BridgeHostOptions options) =>
        Load(
            options,
            Environment.GetEnvironmentVariable,
            path => File.Exists(path) ? File.ReadAllText(path) : null);

    internal static ActiveFeishuFileTransferSettings Load(
        BridgeHostOptions options,
        Func<string, string?> readEnvironment,
        Func<string, string?> readFile)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(readEnvironment);
        ArgumentNullException.ThrowIfNull(readFile);
        var dataDirectory = Path.GetFullPath(options.DataDirectory);
        var configurationDirectory = Path.GetDirectoryName(dataDirectory) ?? dataDirectory;
        var fileValues = ParseEnvironmentFile(
            readFile(Path.Combine(configurationDirectory, ".env")));
        string? Value(string name) =>
            readEnvironment(name) ?? fileValues.GetValueOrDefault(name);

        var ttlMilliseconds = PositiveInt64(
            Value("FEISHU_UPLOAD_TTL_MS"),
            DefaultUploadTtlMilliseconds);
        return new(
            Path.Combine(dataDirectory, "uploads"),
            PositiveInt64(
                Value("FEISHU_INBOUND_FILE_MAX_BYTES"),
                DefaultInboundFileMaxBytes),
            PositiveInt32(
                Value("FEISHU_INBOUND_ATTACHMENT_MAX_COUNT"),
                DefaultInboundAttachmentMaxCount),
            PositiveInt32(
                Value("FEISHU_UPLOAD_MAX_FILES"),
                DefaultUploadMaxFiles),
            PositiveInt64(
                Value("FEISHU_UPLOAD_MAX_BYTES"),
                DefaultUploadMaxBytes),
            TimeSpan.FromMilliseconds(Math.Min(
                ttlMilliseconds,
                (long)TimeSpan.MaxValue.TotalMilliseconds)),
            PositiveInt64(
                Value("FEISHU_OUTBOUND_FILE_MAX_BYTES"),
                DefaultOutboundFileMaxBytes));
    }

    private static int PositiveInt32(string? value, int fallback) =>
        long.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
        parsed is > 0 and <= int.MaxValue
            ? (int)parsed
            : fallback;

    private static long PositiveInt64(string? value, long fallback) =>
        long.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
        parsed > 0
            ? parsed
            : fallback;

    private static IReadOnlyDictionary<string, string> ParseEnvironmentFile(string? content)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (content is null)
        {
            return values;
        }
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }
            if (trimmed.StartsWith("export ", StringComparison.Ordinal))
            {
                trimmed = trimmed[7..].TrimStart();
            }
            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }
            var key = trimmed[..separator].Trim();
            if (!VariableNames.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }
            values[key] = ParseEnvironmentValue(trimmed[(separator + 1)..]);
        }
        return values;
    }

    private static string ParseEnvironmentValue(string source)
    {
        var value = source.Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }
        if (value[0] is not ('\'' or '"' or '`'))
        {
            var comment = value.IndexOf('#');
            return (comment >= 0 ? value[..comment] : value).Trim();
        }
        var quote = value[0];
        var closing = value.LastIndexOf(quote);
        return closing > 0 ? value[1..closing] : value;
    }
}

internal static class BridgeFileTransferProtocol
{
    private const int MaximumFiles = 3;
    private static readonly Regex FileDirective = new(
        @"^\s*BRIDGE_SEND_FILE:\s*(.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ExcessBlankLines = new(
        @"\n{3,}",
        RegexOptions.CultureInvariant);
    private static readonly IReadOnlySet<string> AllowedExtensions =
        new HashSet<string>(
        [
            ".bmp", ".csv", ".doc", ".docx", ".gif", ".ico", ".jpeg",
            ".jpg", ".json", ".log", ".md", ".mp4", ".pdf", ".png",
            ".ppt", ".pptx", ".tif", ".tiff", ".txt", ".webp", ".xls",
            ".xlsx", ".zip",
        ],
        StringComparer.Ordinal);

    public static string AppendAttachmentsToPrompt(
        string prompt,
        IReadOnlyList<BridgeSavedAttachment> attachments)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(attachments);
        if (attachments.Count == 0)
        {
            return prompt;
        }
        var files = string.Join(
            '；',
            attachments.Select((attachment, index) =>
                $"{index + 1}. {attachment.AbsolutePath}"));
        return $"飞书附件已保存到本机：{files}。" +
            $"请使用适合的文件读取工具处理这些文件。用户要求：{prompt}";
    }

    public static string AddFileReturnInstruction(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return prompt +
            "\n\n用户明确要求把最终文件发回飞书。请把生成文件保存到当前项目目录内，" +
            "并在最终回复中为每个文件单独输出一行：BRIDGE_SEND_FILE: 绝对路径。" +
            "最多 3 个文件；不要声明项目目录外的文件。";
    }

    public static BridgeFileDirectiveResult ExtractDirectives(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var paths = new List<string>(MaximumFiles);
        var kept = new List<string>();
        var normalized = message.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        foreach (var line in normalized.Split('\n'))
        {
            var match = FileDirective.Match(line);
            if (!match.Success)
            {
                kept.Add(line);
                continue;
            }
            var candidate = StripWrappingQuotes(match.Groups[1].Value.Trim());
            if (candidate.Length != 0 &&
                paths.Count < MaximumFiles &&
                !paths.Contains(candidate, StringComparer.Ordinal))
            {
                paths.Add(candidate);
            }
        }
        var display = ExcessBlankLines.Replace(string.Join('\n', kept), "\n\n").Trim();
        return new(display, paths);
    }

    public static Task<ValidatedBridgeFile> ValidateFileAsync(
        string candidate,
        string cwd,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }
        if (!Path.IsPathFullyQualified(candidate))
        {
            throw new InvalidDataException("回传路径不是绝对路径。");
        }

        var resolvedFile = ResolveExistingPath(candidate);
        var resolvedRoot = ResolveExistingPath(cwd);
        if (!Directory.Exists(resolvedRoot))
        {
            throw new InvalidDataException("当前项目目录不可用。");
        }
        if (!IsInside(resolvedRoot, resolvedFile))
        {
            throw new InvalidDataException("回传文件不在当前项目目录内。");
        }
        var extension = Path.GetExtension(resolvedFile).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension))
        {
            throw new InvalidDataException(
                $"不允许回传 {(extension.Length == 0 ? "无扩展名" : extension)} 文件。");
        }
        var info = new FileInfo(resolvedFile);
        if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException("回传路径不是普通文件。");
        }
        if (info.Length <= 0 || info.Length > maxBytes)
        {
            throw new InvalidDataException(
                $"回传文件为空或超过 {FormatMegabytes(maxBytes)}。");
        }
        return Task.FromResult(new ValidatedBridgeFile(resolvedFile, info.Length));
    }

    private static string ResolveExistingPath(string path) =>
        ResolveExistingPath(
            Path.GetFullPath(path),
            new HashSet<string>(PathComparer()),
            0);

    private static string ResolveExistingPath(
        string fullPath,
        HashSet<string> resolving,
        int depth)
    {
        if (depth > 32 || !resolving.Add(fullPath))
        {
            throw new IOException("回传路径包含无法解析的链接循环。");
        }
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            throw new InvalidDataException("回传路径不是绝对路径。");
        }
        var current = root;
        var relative = fullPath[root.Length..];
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var next = Path.Combine(current, segment);
            FileSystemInfo info;
            if (Directory.Exists(next))
            {
                info = new DirectoryInfo(next);
            }
            else if (File.Exists(next))
            {
                info = new FileInfo(next);
            }
            else
            {
                throw new FileNotFoundException("回传路径不存在。", next);
            }
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            current = target is null
                ? Path.GetFullPath(next)
                : ResolveExistingPath(
                    Path.GetFullPath(target.FullName),
                    resolving,
                    depth + 1);
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    private static bool IsInside(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative.Length != 0 &&
            !Path.IsPathRooted(relative) &&
            !string.Equals(relative, "..", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string StripWrappingQuotes(string value)
    {
        if (value.Length >= 2 &&
            (value[0] == '"' && value[^1] == '"' ||
             value[0] == '\'' && value[^1] == '\''))
        {
            return value[1..^1].Trim();
        }
        return value;
    }

    private static string FormatMegabytes(long bytes)
    {
        var mebibytes = bytes / (1024d * 1024d);
        return $"{Math.Floor(mebibytes + 0.5):0} MiB";
    }

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal sealed record ValidatedBridgeFile(string Path, long Size);
}

internal sealed class LocalAttachmentStore : IDisposable
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

    private static void EnsureSafeDirectoryRoot(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        var missing = new Stack<string>();
        var current = fullPath;
        while (!Directory.Exists(current))
        {
            missing.Push(current);
            var parent = Directory.GetParent(current);
            if (parent is null || PathComparer().Equals(parent.FullName, current))
            {
                throw new DirectoryNotFoundException(
                    $"附件暂存区的父目录不存在：{current}");
            }
            current = parent.FullName;
        }

        EnsureNotReparseDirectory(current);
        while (missing.Count > 0)
        {
            current = missing.Pop();
            Directory.CreateDirectory(current);
            EnsureNotReparseDirectory(current);
        }
    }

    private static void EnsureNotReparseDirectory(string directory)
    {
        var info = new DirectoryInfo(directory);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("附件暂存区不能位于重解析目录中。");
        }
    }

    private static DateTimeOffset SafeCutoff(
        DateTimeOffset now,
        TimeSpan ttl) =>
        ttl >= now - DateTimeOffset.MinValue
            ? DateTimeOffset.MinValue
            : now - ttl;

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static string FormatBytes(long value)
    {
        if (value >= 1024L * 1024 * 1024)
        {
            return $"{value / (1024d * 1024 * 1024):0.0} GiB";
        }
        if (value >= 1024L * 1024)
        {
            return $"{value / (1024d * 1024):0.0} MiB";
        }
        if (value >= 1024)
        {
            return $"{value / 1024d:0.0} KiB";
        }
        return $"{value} B";
    }

    private sealed class DirectoryUsage
    {
        public int FileCount { get; set; }

        public long TotalBytes { get; set; }
    }
}
