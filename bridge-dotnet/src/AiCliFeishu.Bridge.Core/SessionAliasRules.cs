using System.Text;
using System.Text.RegularExpressions;

namespace AiCliFeishu.Bridge.Core;

/// <summary>
/// Shared normalization and validation rules for the alias stored in the
/// Persistent session document. Keeping these rules in Core prevents a
/// Feishu command path and a persistence path from assigning different keys.
/// </summary>
public static partial class SessionAliasRules
{
    public const int MaximumLength = 20;

    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Trim().Normalize(NormalizationForm.FormC);
    }

    public static string Key(string value) =>
        Normalize(value).ToLowerInvariant();

    public static string? ValidationError(string value)
    {
        var alias = Normalize(value);
        if (alias.Length == 0)
        {
            return "别名不能为空。";
        }
        if (alias.EnumerateRunes().Count() > MaximumLength)
        {
            return $"别名最多 {MaximumLength} 个字符。";
        }
        if (!AllowedCharacters().IsMatch(alias))
        {
            return "别名只能包含中文、字母、数字、下划线或短横线，不能包含空格。";
        }
        return null;
    }

    [GeneratedRegex(@"^[\p{L}\p{N}_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedCharacters();
}
