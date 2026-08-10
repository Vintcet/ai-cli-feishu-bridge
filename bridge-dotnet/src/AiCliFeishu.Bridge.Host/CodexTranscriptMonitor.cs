using System.Text;
using System.Text.Json;

namespace AiCliFeishu.Bridge.Host;

internal sealed record CodexTranscriptErrorEvent(
    string SessionId,
    string TurnId,
    string TranscriptPath,
    string Error,
    string? ErrorCode);

internal sealed class CodexTranscriptMonitor :
    IBridgeHostSubsystem,
    IBridgeHostSubsystemHealth,
    IBridgeBackgroundSubsystem
{
    private const int MaximumReadBytes = 4 * 1024 * 1024;
    private readonly object sync = new();
    private readonly Dictionary<string, WatchState> watches = new(StringComparer.Ordinal);
    private readonly TimeSpan activeInterval;
    private readonly TimeSpan idleInterval;
    private readonly TimeSpan activeWindow;
    private readonly string cursorPath;
    private readonly SemaphoreSlim cursorGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly bool cursorRecoveryRequired;
    private Dictionary<string, PersistedCursor> cursors;
    private Func<CodexTranscriptErrorEvent, CancellationToken, Task>? onError;
    private Task? loop;
    private bool started;

    public CodexTranscriptMonitor(BridgeHostOptions options)
    {
        cursorPath = Path.Combine(
            options.DataDirectory,
            "codex-transcript-cursors.json");
        var loadedCursors = LoadCursors(cursorPath);
        cursors = loadedCursors.Cursors;
        cursorRecoveryRequired = loadedCursors.RecoveryRequired;
        activeInterval = ConfigurationDuration(
            options,
            "CODEX_TRANSCRIPT_POLL_INTERVAL_MS",
            750,
            50);
        idleInterval = ConfigurationDuration(
            options,
            "CODEX_TRANSCRIPT_IDLE_POLL_INTERVAL_MS",
            5_000,
            (int)activeInterval.TotalMilliseconds);
        activeWindow = ConfigurationDuration(
            options,
            "CODEX_TRANSCRIPT_ACTIVE_WINDOW_MS",
            30_000,
            (int)activeInterval.TotalMilliseconds);
    }

    public string Name => "codex-transcript-monitor";

    public Task? Completion => loop;

    public BridgeComponentHealth ComponentHealth
    {
        get
        {
            lock (sync)
            {
                return new(Name, started ? "ready" : "starting", $"watches={watches.Count}");
            }
        }
    }

    public void Attach(Func<CodexTranscriptErrorEvent, CancellationToken, Task> handler) =>
        onError = handler ?? throw new ArgumentNullException(nameof(handler));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (started)
            {
                return Task.CompletedTask;
            }
            if (onError is null)
            {
                throw new InvalidOperationException("Codex 转录监控器尚未连接事件处理器。");
            }
            started = true;
            loop = RunAsync(lifetime.Token);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? current;
        lock (sync)
        {
            if (!started)
            {
                return;
            }
            started = false;
            lifetime.Cancel();
            current = loop;
        }
        if (current is not null)
        {
            try
            {
                await current.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
        WatchState[] remaining;
        lock (sync)
        {
            remaining = watches.Values.ToArray();
            watches.Clear();
            loop = null;
        }
        foreach (var state in remaining)
        {
            await ScanBestEffortAsync(state, CancellationToken.None);
        }
    }

    public async Task<bool> WatchAsync(
        string sessionId,
        string? transcriptPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(transcriptPath) ||
            !Path.IsPathFullyQualified(transcriptPath) ||
            !string.Equals(Path.GetExtension(transcriptPath), ".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var path = Path.GetFullPath(transcriptPath);
        long offset;
        DateTime creation = DateTime.MinValue;
        long length = 0;
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                length = info.Length;
                creation = info.CreationTimeUtc;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (cursors.TryGetValue(sessionId, out var cursor) &&
                string.Equals(cursor.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                var sameFile = cursor.CreationTimeUtc == DateTime.MinValue ||
                    creation == DateTime.MinValue ||
                    cursor.CreationTimeUtc == creation;
                offset = sameFile && cursor.Offset <= length
                    ? Math.Max(0, cursor.Offset)
                    : 0;
            }
            else
            {
                // A session/path pair without a durable cursor is a first watch:
                // ignore existing history only when the cursor store was loaded
                // cleanly. If it was unreadable or corrupt, fail closed and
                // replay from the beginning rather than silently losing events.
                offset = cursorRecoveryRequired ? 0 : length;
            }
        }
        WatchState state;
        lock (sync)
        {
            if (watches.TryGetValue(sessionId, out var existing) &&
                string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                existing.ActiveUntil = DateTimeOffset.UtcNow + activeWindow;
                return true;
            }
            state = new(
                sessionId,
                path,
                offset,
                creation,
                DateTimeOffset.UtcNow + activeWindow);
            watches[sessionId] = state;
        }
        try
        {
            await CommitCursorAsync(state, offset, creation, cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            lock (sync)
            {
                if (watches.GetValueOrDefault(sessionId) == state)
                {
                    watches.Remove(sessionId);
                }
            }
            return false;
        }
        return true;
    }

    public async Task UnwatchAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        WatchState? state;
        lock (sync)
        {
            watches.Remove(sessionId, out state);
        }
        if (state is not null)
        {
            await ScanBestEffortAsync(state, cancellationToken);
        }
    }

    internal async Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        WatchState[] states;
        lock (sync)
        {
            states = watches.Values.ToArray();
        }
        foreach (var state in states)
        {
            await ScanBestEffortAsync(state, cancellationToken);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await CheckNowAsync(cancellationToken);
            var active = false;
            lock (sync)
            {
                active = watches.Values.Any(state =>
                    state.ActiveUntil > DateTimeOffset.UtcNow);
            }
            await Task.Delay(active ? activeInterval : idleInterval, cancellationToken);
        }
    }

    private async Task ScanBestEffortAsync(WatchState state, CancellationToken cancellationToken)
    {
        try
        {
            await state.ScanGate.WaitAsync(cancellationToken);
            try
            {
                await ScanAsync(state, cancellationToken);
            }
            finally
            {
                state.ScanGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The cursor only advances after a complete line and its callback
            // have succeeded. Any transient scan/handler failure is therefore
            // safe to retry on the next poll without terminating the Host.
        }
    }

    private async Task ScanAsync(WatchState state, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            state.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var info = new FileInfo(state.Path);
        if ((state.CreationTimeUtc != DateTime.MinValue &&
             info.CreationTimeUtc != state.CreationTimeUtc) ||
            stream.Length < state.Offset)
        {
            await CommitCursorAsync(
                state,
                0,
                info.CreationTimeUtc,
                cancellationToken);
        }
        state.CreationTimeUtc = info.CreationTimeUtc;
        while (stream.Length > state.Offset)
        {
            state.ActiveUntil = DateTimeOffset.UtcNow + activeWindow;
            var start = state.Offset;
            stream.Position = start;
            var requested = checked((int)Math.Min(
                MaximumReadBytes,
                stream.Length - start));
            var buffer = new byte[requested];
            var read = 0;
            while (read < requested)
            {
                var count = await stream.ReadAsync(
                    buffer.AsMemory(read, requested - read),
                    cancellationToken);
                if (count == 0)
                {
                    break;
                }
                read += count;
            }
            if (read == 0)
            {
                return;
            }

            var lineStart = 0;
            var batchOffset = start;
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] != (byte)'\n')
                {
                    continue;
                }
                var line = Encoding.UTF8.GetString(
                        buffer,
                        lineStart,
                        index - lineStart)
                    .TrimEnd('\r');
                var parsed = false;
                string? turnId = null;
                string? error = null;
                string? code = null;
                try
                {
                    parsed = TryParseError(line, out turnId, out error, out code);
                }
                catch (JsonException)
                {
                    // One malformed JSONL record is isolated to that record. It
                    // must not prevent later valid transcript entries from being
                    // observed and committed.
                }
                if (parsed)
                {
                    try
                    {
                        await onError!(new(
                            state.SessionId,
                            turnId ?? $"transcript-{start + index + 1}",
                            state.Path,
                            error!,
                            code), cancellationToken);
                    }
                    catch
                    {
                        // Commit only the successfully handled prefix. The
                        // failing line itself remains replayable on the next poll.
                        await CommitCursorAsync(
                            state,
                            batchOffset,
                            info.CreationTimeUtc,
                            cancellationToken);
                        throw;
                    }
                }
                batchOffset = start + index + 1;
                lineStart = index + 1;
            }

            if (lineStart < read)
            {
                if (lineStart == 0 && read == MaximumReadBytes)
                {
                    // An individual record larger than the safety limit cannot
                    // be parsed as a bounded JSONL line. Advance through it in
                    // bounded chunks so it cannot wedge every future scan.
                    batchOffset = start + read;
                }
            }

            if (batchOffset > start)
            {
                // A read batch performs at most one durable cursor rewrite.
                // Crashes before this point may replay successful callbacks,
                // which is intentional at-least-once delivery.
                await CommitCursorAsync(
                    state,
                    batchOffset,
                    info.CreationTimeUtc,
                    cancellationToken);
            }
            if (lineStart < read && lineStart != 0)
            {
                return;
            }
            if (lineStart == 0 && read < MaximumReadBytes)
            {
                return;
            }
        }
    }

    private static bool TryParseError(
        string line,
        out string? turnId,
        out string? error,
        out string? errorCode)
    {
        turnId = null;
        error = null;
        errorCode = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }
        using var document = JsonDocument.Parse(line);
        var value = document.RootElement;
        if (value.TryGetProperty("type", out var rootType) &&
            rootType.GetString() == "event_msg" &&
            value.TryGetProperty("payload", out var payload))
        {
            value = payload;
        }
        if (!value.TryGetProperty("type", out var type) ||
            type.GetString() != "task_complete" ||
            !value.TryGetProperty("error", out var errorValue))
        {
            return false;
        }
        turnId = String(value, "turn_id");
        if (errorValue.ValueKind == JsonValueKind.String)
        {
            error = errorValue.GetString()?.Trim();
        }
        else if (errorValue.ValueKind == JsonValueKind.Object)
        {
            error = String(errorValue, "message");
            errorCode = String(errorValue, "codex_error_info") ??
                String(errorValue, "code") ?? String(errorValue, "type");
        }
        error ??= errorCode is null ? null : $"Codex error: {errorCode}";
        return !string.IsNullOrWhiteSpace(error);
    }

    private static string? String(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!.Trim()
            : null;

    private async Task CommitCursorAsync(
        WatchState state,
        long offset,
        DateTime creationTimeUtc,
        CancellationToken cancellationToken)
    {
        await cursorGate.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, PersistedCursor> updated;
            lock (sync)
            {
                updated = new Dictionary<string, PersistedCursor>(
                    cursors,
                    StringComparer.Ordinal)
                {
                    [state.SessionId] = new(
                        state.Path,
                        Math.Max(0, offset),
                        creationTimeUtc),
                };
            }
            var directory = Path.GetDirectoryName(cursorPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var temporary = $"{cursorPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(updated);
                await using (var stream = new FileStream(
                    temporary,
                    new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                        BufferSize = 16 * 1024,
                        Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
                    }))
                {
                    await stream.WriteAsync(payload, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, cursorPath, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
                catch
                {
                }
            }
            lock (sync)
            {
                cursors = updated;
                state.Offset = Math.Max(0, offset);
                state.CreationTimeUtc = creationTimeUtc;
            }
        }
        finally
        {
            cursorGate.Release();
        }
    }

    private static CursorLoadResult LoadCursors(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new(new(StringComparer.Ordinal), false);
            }
            var loaded = JsonSerializer.Deserialize<Dictionary<string, PersistedCursor>>(
                File.ReadAllText(path));
            if (loaded is null)
            {
                return new(new(StringComparer.Ordinal), true);
            }
            var valid = loaded
                .Where(item =>
                    !string.IsNullOrWhiteSpace(item.Key) &&
                    item.Value is not null &&
                    !string.IsNullOrWhiteSpace(item.Value.Path) &&
                    Path.IsPathFullyQualified(item.Value.Path) &&
                    item.Value.Offset >= 0)
                .ToDictionary(
                    item => item.Key,
                    item => item.Value,
                    StringComparer.Ordinal);
            return new(valid, valid.Count != loaded.Count);
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or JsonException or
                NotSupportedException or ArgumentException)
        {
            return new(new(StringComparer.Ordinal), true);
        }
    }

    private static TimeSpan ConfigurationDuration(
        BridgeHostOptions options,
        string name,
        int fallback,
        int minimum)
    {
        var value = BridgeLocalConfiguration.Read(options, name);
        return TimeSpan.FromMilliseconds(
            int.TryParse(value, out var parsed) && parsed >= minimum
                ? parsed
                : fallback);
    }

    private sealed class WatchState(
        string sessionId,
        string path,
        long offset,
        DateTime creationTimeUtc,
        DateTimeOffset activeUntil)
    {
        public string SessionId { get; } = sessionId;
        public string Path { get; } = path;
        public long Offset { get; set; } = offset;
        public DateTime CreationTimeUtc { get; set; } = creationTimeUtc;
        public DateTimeOffset ActiveUntil { get; set; } = activeUntil;
        public SemaphoreSlim ScanGate { get; } = new(1, 1);
    }

    private sealed record PersistedCursor(
        string Path,
        long Offset,
        DateTime CreationTimeUtc);

    private sealed record CursorLoadResult(
        Dictionary<string, PersistedCursor> Cursors,
        bool RecoveryRequired);
}
