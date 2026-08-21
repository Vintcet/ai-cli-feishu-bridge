using System.Text;
using System.Text.Json;

namespace AiCliFeishu.Bridge.Host;

internal interface IManagedHookRequestLog
{
    Task AppendAsync(
        string kind,
        string? sessionId,
        string? terminalId,
        int statusCode,
        string? failureReason,
        string traceId,
        CancellationToken cancellationToken = default);
}

// Managed hook requests had no log at all: a rejected hook only produced a generic
// "当前不可处理" body, so the actual reason (which identity field mismatched, which
// validation tripped) was discarded and the relay-side error in the CLI transcript was
// the only trace. This records one line per request, metadata and reason only - never
// the payload, which carries prompts and file contents.
internal sealed class ManagedHookRequestLog(BridgeHostOptions options) : IManagedHookRequestLog
{
    private const long MaximumBytes = 5 * 1024 * 1024;
    private const int MaximumBackups = 5;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly string path = Path.Combine(options.DataDirectory, "hook-events.log");

    public async Task AppendAsync(
        string kind,
        string? sessionId,
        string? terminalId,
        int statusCode,
        string? failureReason,
        string traceId,
        CancellationToken cancellationToken = default)
    {
        var line = JsonSerializer.Serialize(new
        {
            at = DateTimeOffset.UtcNow.ToString("O"),
            kind,
            sessionId,
            terminalId,
            statusCode,
            ok = statusCode is >= 200 and < 300,
            reason = Truncate(failureReason),
            traceId,
        }) + "\n";
        try
        {
            await writeGate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes(line);
            if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > MaximumBytes)
            {
                Rotate();
            }
            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read);
            await stream.WriteAsync(bytes, cancellationToken);
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must never break the hook path.
        }
        finally
        {
            writeGate.Release();
        }
    }

    internal static string? Truncate(string? value) => value is null
        ? null
        : value.Length <= 500
            ? value
            : $"{value[..480]}…（已截断）";

    private void Rotate()
    {
        var oldest = $"{path}.{MaximumBackups}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }
        for (var index = MaximumBackups - 1; index >= 1; index--)
        {
            var source = $"{path}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, $"{path}.{index + 1}", overwrite: true);
            }
        }
        File.Move(path, $"{path}.1", overwrite: true);
    }
}
