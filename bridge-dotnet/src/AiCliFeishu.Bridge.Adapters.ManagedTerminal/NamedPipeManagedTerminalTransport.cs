using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Adapters.ManagedTerminal;

public sealed class NamedPipeManagedTerminalTransport(
    TimeSpan? connectTimeout = null) : IManagedTerminalTransport
{
    private readonly TimeSpan timeout = connectTimeout ?? TimeSpan.FromSeconds(7);

    public async Task SendAsync(
        RuntimeCommandContext context,
        ManagedTerminalTarget target,
        string prompt,
        ManagedTerminalSubmitMode submitMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prompt);
        ValidateTarget(target);
        var normalizedPrompt = prompt.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalizedPrompt.Length is 0 or > 8_000)
        {
            throw new ArgumentException("回复内容为空或超过 8000 字。", nameof(prompt));
        }

        await using var pipe = new NamedPipeClientStream(
            ".",
            $"AiCliFeishu.{target.TerminalId}",
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        await pipe.ConnectAsync(timeoutCancellation.Token);

        await using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
        };
        var request = JsonSerializer.Serialize(new
        {
            type = "prompt",
            prompt = normalizedPrompt,
            submitMode = submitMode == ManagedTerminalSubmitMode.Queue ? "queue" : "steer",
        });
        await writer.WriteLineAsync(request.AsMemory(), cancellationToken);

        using var reader = new StreamReader(
            pipe,
            new UTF8Encoding(false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        var line = await reader.ReadLineAsync(timeoutCancellation.Token);
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new IOException("托管终端未返回结果。");
        }
        using var response = JsonDocument.Parse(line);
        var root = response.RootElement;
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
        {
            return;
        }
        var error = root.TryGetProperty("error", out var errorElement) &&
            errorElement.ValueKind == JsonValueKind.String
                ? errorElement.GetString()
                : null;
        throw new IOException(error ?? "托管终端没有接受这条回复。");
    }

    private static void ValidateTarget(ManagedTerminalTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.Ready)
        {
            throw new InvalidOperationException("同步窗口仍在启动或已经离线。");
        }
        if (target.TerminalId.Length is < 8 or > 64 ||
            target.TerminalId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new ArgumentException("托管终端 ID 无效。", nameof(target));
        }
    }
}
