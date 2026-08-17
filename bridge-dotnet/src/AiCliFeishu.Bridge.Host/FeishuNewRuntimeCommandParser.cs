using System.Text.RegularExpressions;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed record FeishuNewRuntimeCommand(
    string Runtime,
    string ProjectName);

internal static partial class FeishuNewRuntimeCommandParser
{
    public static FeishuNewRuntimeCommand? Parse(string? text)
    {
        var match = NewRuntimeCommand().Match(text?.Trim() ?? string.Empty);
        if (!match.Success)
        {
            return null;
        }
        var runtimeText = Whitespace().Replace(
            match.Groups[1].Value,
            string.Empty).ToLowerInvariant();
        var runtime = runtimeText switch
        {
            "codex" => RuntimeNames.Codex,
            "opencode" => RuntimeNames.OpenCode,
            _ => RuntimeNames.ClaudeCode,
        };
        return new(runtime, StripMatchingQuotes(match.Groups[2].Value.Trim()));
    }

    public static string Usage() =>
        "用法：新建 codex 项目名\n" +
        "也支持：新建 claude 项目名、新建 opencode 项目名。\n" +
        "项目会放在电脑端“设置”中的默认工作区；目录不存在时自动创建。";

    private static string StripMatchingQuotes(string value)
    {
        if (value.Length >= 2 &&
            (value[0] == '"' && value[^1] == '"' ||
             value[0] == '\'' && value[^1] == '\''))
        {
            return value[1..^1].Trim();
        }
        return value;
    }

    [GeneratedRegex(
        "^新建\\s+(claude\\s+code|open\\s+code|codex|claude|claudecode|opencode)\\s+([\\s\\S]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NewRuntimeCommand();

    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();
}
