using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiCliFeishuControl;

internal enum TerminalSubmitMode
{
    Steer,
    Queue,
}

internal sealed record TerminalInputRequest(
    string CommandId,
    string TerminalSecret,
    string Prompt,
    TerminalSubmitMode SubmitMode);

internal sealed record TerminalInputResponse(
    string CommandId,
    bool Ok,
    string? Error);

internal static class TerminalInputParser
{
    public static TerminalInputRequest Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new InvalidOperationException("没有收到回复内容。");
        }
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var type) || type.GetString() != "prompt" ||
            !root.TryGetProperty("commandId", out var commandIdElement) ||
            !root.TryGetProperty("terminalSecret", out var terminalSecretElement) ||
            !root.TryGetProperty("prompt", out var promptElement))
        {
            throw new InvalidOperationException("托管终端请求格式不正确。");
        }
        var commandId = commandIdElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(commandId) ||
            commandId.Length > 256 ||
            commandId.Any(char.IsControl))
        {
            throw new InvalidOperationException("托管终端命令 ID 无效。");
        }
        var terminalSecret = terminalSecretElement.GetString()?.Trim();
        if (!TerminalProtocolSecurity.IsValidSecret(terminalSecret))
        {
            throw new InvalidOperationException("托管终端请求密钥无效。");
        }
        var prompt = promptElement.GetString()?.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 8_000)
        {
            throw new InvalidOperationException("回复内容为空或过长。");
        }

        var rawMode = root.TryGetProperty("submitMode", out var modeElement)
            ? modeElement.GetString()
            : "steer";
        var submitMode = rawMode?.ToLowerInvariant() switch
        {
            null or "" or "steer" => TerminalSubmitMode.Steer,
            "queue" => TerminalSubmitMode.Queue,
            _ => throw new InvalidOperationException("托管终端提交模式无效。"),
        };
        return new TerminalInputRequest(commandId, terminalSecret!, prompt, submitMode);
    }
}

internal static class TerminalProtocolSecurity
{
    public static string GenerateSecret() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static bool IsValidSecret(string? value) =>
        value?.Length == 64 && value.All(Uri.IsHexDigit);

    public static bool SecretEquals(string left, string right)
    {
        if (!IsValidSecret(left) || !IsValidSecret(right))
        {
            return false;
        }
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

internal sealed class TerminalCommandResultCache(int maximumEntries = 256)
{
    private readonly object sync = new();
    private readonly Dictionary<string, CachedResult> results = new(StringComparer.Ordinal);
    private readonly Queue<string> insertionOrder = new();

    public TerminalInputResponse Execute(
        TerminalInputRequest input,
        Action inject)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(inject);
        var signature = Signature(input);
        lock (sync)
        {
            if (results.TryGetValue(input.CommandId, out var cached))
            {
                return string.Equals(cached.Signature, signature, StringComparison.Ordinal)
                    ? cached.Response
                    : new(
                        input.CommandId,
                        false,
                        "托管终端命令 ID 已被用于不同内容。");
            }

            TerminalInputResponse response;
            try
            {
                inject();
                response = new(input.CommandId, true, null);
            }
            catch (Exception error)
            {
                response = new(input.CommandId, false, error.Message);
            }
            results.Add(input.CommandId, new(signature, response));
            insertionOrder.Enqueue(input.CommandId);
            while (results.Count > maximumEntries && insertionOrder.TryDequeue(out var oldest))
            {
                results.Remove(oldest);
            }
            return response;
        }
    }

    private static string Signature(TerminalInputRequest input)
    {
        var source = $"{(int)input.SubmitMode}\0{input.Prompt}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    private sealed record CachedResult(string Signature, TerminalInputResponse Response);
}

internal static class TerminalPipeProtocol
{
    public const int MaximumRequestBytes = 64 * 1024;
    public static TimeSpan RequestReadTimeout { get; } = TimeSpan.FromSeconds(5);

    public static async Task<string?> ReadLineAsync(
        Stream stream,
        int maximumBytes = MaximumRequestBytes,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }
        var readTimeout = timeout ?? RequestReadTimeout;
        if (readTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCancellation.CancelAfter(readTimeout);
        var buffer = new byte[4_096];
        using var output = new MemoryStream(Math.Min(maximumBytes, buffer.Length));
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, timeoutCancellation.Token);
                if (read == 0)
                {
                    return output.Length == 0 ? null : DecodeLine(output);
                }
                var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
                var count = newline >= 0 ? newline : read;
                if (output.Length + count > maximumBytes)
                {
                    throw new InvalidDataException(
                        $"托管终端请求不能超过 {maximumBytes} 字节。");
                }
                output.Write(buffer, 0, count);
                if (newline >= 0)
                {
                    return DecodeLine(output);
                }
            }
        }
        catch (OperationCanceledException error) when (
            !cancellationToken.IsCancellationRequested &&
            timeoutCancellation.IsCancellationRequested)
        {
            throw new TimeoutException("读取托管终端请求超时。", error);
        }
    }

    private static string DecodeLine(MemoryStream output)
    {
        var bytes = output.ToArray();
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
        {
            Array.Resize(ref bytes, bytes.Length - 1);
        }
        return new UTF8Encoding(false, true).GetString(bytes);
    }
}
