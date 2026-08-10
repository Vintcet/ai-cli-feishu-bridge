using System.Text;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Core;

/// <summary>
/// Stable names for assistant-created Feishu session groups.
/// Keeping the calculation in Core makes every Active command path use the
/// same runtime prefix, alias fallback and ordinal rules.
/// </summary>
public static class SessionGroupNameRules
{
    public const int MaximumLength = 60;

    public static string Build(
        string? runtime,
        string? alias,
        string? projectName,
        string? shortId,
        int? ordinal = null)
    {
        var prefix = RuntimePrefix(runtime);
        var hasAlias = !string.IsNullOrWhiteSpace(alias);
        var baseName = FirstNonEmpty(alias, projectName, shortId) ?? "会话";
        var suffix = !hasAlias && ordinal is > 1
            ? $"（{ordinal.Value}）"
            : string.Empty;
        var available = Math.Max(
            0,
            MaximumLength - prefix.Length - suffix.Length);
        return prefix + Truncate(baseName, available) + suffix;
    }

    public static string RuntimePrefix(string? runtime) => runtime switch
    {
        RuntimeNames.ClaudeCode => "Claude｜",
        RuntimeNames.OpenCode => "OpenCode｜",
        _ => "Codex｜",
    };

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string Truncate(string value, int maximumLength)
    {
        if (maximumLength <= 0)
        {
            return string.Empty;
        }
        if (value.Length <= maximumLength)
        {
            return value;
        }

        // Keep the established UTF-16 length cap while
        // avoiding a dangling surrogate when a project name contains emoji.
        var length = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (length + rune.Utf16SequenceLength > maximumLength)
            {
                break;
            }
            length += rune.Utf16SequenceLength;
        }
        return value[..length];
    }
}
