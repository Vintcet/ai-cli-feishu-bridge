using System.Text.RegularExpressions;

namespace AiCliFeishu.Bridge.Adapters.Feishu;

public enum FeishuAliasTargetKind
{
    ShortId,
    Alias,
}

public sealed record FeishuAliasCommand(
    FeishuAliasTargetKind? TargetKind = null,
    string? Target = null,
    string? Alias = null);

/// <summary>
/// Parses the small text command which is intentionally kept outside the
/// standard business Core.  A null result means the text looked like an alias
/// command but was malformed (or was not an alias command at all); callers can
/// then return the stable usage text instead of guessing a target.
/// </summary>
public static partial class FeishuAliasCommandParser
{
    public static FeishuAliasCommand? Parse(string? text)
    {
        var command = text?.Trim();
        if (command is null)
        {
            return null;
        }
        if (command == "别名")
        {
            return new();
        }

        var match = CommandPattern().Match(command);
        if (!match.Success)
        {
            return null;
        }

        var marker = match.Groups[1].Value;
        var target = match.Groups[2].Value;
        if (marker == "#")
        {
            if (!ShortIdPattern().IsMatch(target))
            {
                return null;
            }
            target = target.ToLowerInvariant();
        }

        var alias = match.Groups[3].Success
            ? match.Groups[3].Value.Trim()
            : null;
        return new(
            marker == "#"
                ? FeishuAliasTargetKind.ShortId
                : FeishuAliasTargetKind.Alias,
            target,
            alias);
    }

    public static bool IsListCommand(string? text)
    {
        var command = text?.Trim();
        return command is "/aliases" or "/别名" or "/会话别名" or "别名";
    }

    public static string Usage() =>
        "设置：别名 #短ID 名称\n" +
        "清除：别名 #短ID 清除\n" +
        "也可用旧别名定位：别名 @旧别名 新名称\n" +
        "回复：@名称 你的内容\n" +
        "规则：1–20 个字符，可用中文、字母、数字、下划线和短横线。";

    [GeneratedRegex(
        @"^别名\s+([#@])([^\s#@]+)(?:\s+([\s\S]+))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CommandPattern();

    [GeneratedRegex(@"^[a-zA-Z0-9]{4,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ShortIdPattern();
}
