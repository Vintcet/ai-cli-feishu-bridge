using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Adapters.ManagedTerminal;

public sealed class ManagedTerminalUnavailableException : IOException
{
    public ManagedTerminalUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

public sealed class ManagedTerminalRejectedException : IOException
{
    public ManagedTerminalRejectedException(string message)
        : base(message) { }
}

public sealed class NamedPipeManagedTerminalTransport : IManagedTerminalTransport
{
    private readonly Func<ManagedTerminalTarget, bool>? isCurrent;
    private readonly TimeSpan timeout;

    public NamedPipeManagedTerminalTransport(
        TimeSpan? connectTimeout = null,
        Func<ManagedTerminalTarget, bool>? isCurrent = null)
    {
        timeout = connectTimeout ?? TimeSpan.FromSeconds(7);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(connectTimeout),
                "托管终端连接超时必须大于零。");
        }
        this.isCurrent = isCurrent;
    }

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
        if (submitMode is not ManagedTerminalSubmitMode.Steer and
            not ManagedTerminalSubmitMode.Queue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(submitMode),
                "托管终端提交模式无效。");
        }
        EnsureCurrent(target);
        var normalizedPrompt = NormalizePrompt(prompt);
        if (normalizedPrompt.Length is 0 or > 8_000)
        {
            throw new ArgumentException("回复内容为空或超过 8000 字。", nameof(prompt));
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                $"AiCliFeishu.{target.TerminalId}",
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeoutCancellation.Token);
            EnsureCurrent(target);

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
            await writer.WriteLineAsync(
                request.AsMemory(),
                timeoutCancellation.Token);

            using var reader = new StreamReader(
                pipe,
                new UTF8Encoding(false),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024,
                leaveOpen: true);
            var line = await reader.ReadLineAsync(timeoutCancellation.Token);
            if (line is null)
            {
                throw new ManagedTerminalUnavailableException(
                    "托管终端在返回结果前关闭了连接。");
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new ManagedTerminalRejectedException(
                    "托管终端返回了无法识别的结果。");
            }
            using var response = JsonDocument.Parse(line);
            var root = response.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                throw new JsonException("托管终端返回结果必须是 JSON 对象。");
            }
            if (root.TryGetProperty("ok", out var ok) &&
                ok.ValueKind == JsonValueKind.True)
            {
                return;
            }
            var error = root.TryGetProperty("error", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.String
                    ? errorElement.GetString()
                    : null;
            throw new ManagedTerminalRejectedException(
                string.IsNullOrWhiteSpace(error)
                    ? "托管终端没有接受这条回复。"
                    : error);
        }
        catch (OperationCanceledException error) when (
            !cancellationToken.IsCancellationRequested &&
            timeoutCancellation.IsCancellationRequested)
        {
            throw new ManagedTerminalUnavailableException(
                "连接托管同步窗口超时。",
                error);
        }
        catch (UnauthorizedAccessException)
        {
            throw new ManagedTerminalRejectedException(
                "管理员同步窗口拒绝了桥接连接。请用最新版桌面助手重新打开这个会话。");
        }
        catch (IOException error) when (
            error is not ManagedTerminalUnavailableException and
            not ManagedTerminalRejectedException)
        {
            throw new ManagedTerminalUnavailableException(
                "对应的同步窗口暂时无法接收输入。",
                error);
        }
    }

    private void EnsureCurrent(ManagedTerminalTarget target)
    {
        if (isCurrent is not null && !isCurrent(target))
        {
            throw new InvalidOperationException(
                "托管终端与目标会话不匹配，已拒绝输入以避免串线。");
        }
    }

    private static void ValidateTarget(ManagedTerminalTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.Ready)
        {
            throw new InvalidOperationException("同步窗口仍在启动或已经离线。");
        }
        if (string.IsNullOrEmpty(target.TerminalId) ||
            target.TerminalId.Length is < 8 or > 64 ||
            target.TerminalId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new ArgumentException("托管终端 ID 无效。", nameof(target));
        }
        if (string.IsNullOrWhiteSpace(target.SessionExternalId) ||
            target.SessionExternalId.Length > 256 ||
            target.SessionExternalId.Any(char.IsControl))
        {
            throw new ArgumentException("托管终端会话 ID 无效。", nameof(target));
        }
    }

    private static string NormalizePrompt(string prompt)
    {
        var normalized = new StringBuilder(prompt.Length);
        var lineBreak = false;
        foreach (var character in prompt)
        {
            if (character is '\r' or '\n')
            {
                if (!lineBreak)
                {
                    normalized.Append(' ');
                    lineBreak = true;
                }
                continue;
            }
            normalized.Append(character);
            lineBreak = false;
        }
        return normalized.ToString().Trim();
    }
}
