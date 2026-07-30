using System.Text.Json;

namespace CodexFeishuControl;

internal enum TerminalSubmitMode
{
    Steer,
    Queue,
}

internal sealed record TerminalInputRequest(string Prompt, TerminalSubmitMode SubmitMode);

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
            !root.TryGetProperty("prompt", out var promptElement))
        {
            throw new InvalidOperationException("托管终端请求格式不正确。");
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
        return new TerminalInputRequest(prompt, submitMode);
    }
}
