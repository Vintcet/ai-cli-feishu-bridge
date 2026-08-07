using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace AiCliFeishu.Bridge.Adapters.Feishu;

/// <summary>
/// The small Markdown-to-card boundary used by runtime message notifications.
/// Feishu's card Markdown is deliberately narrower than the Markdown emitted by
/// the CLIs, so headings, lists, code fences, quotes and tables are normalized
/// before they become card elements.
/// </summary>
internal static partial class FeishuMarkdownCards
{
    public const int MessageChunkLength = 2_800;

    private const int MaximumNativeTables = 5;
    private const int MaximumTableColumns = 50;
    private static readonly Regex ansi = new(
        @"\u001b\[[0-?]*[ -/]*[@-~]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex heading = new(
        @"^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex unordered = new(
        @"^\s*[-+*]\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ordered = new(
        @"^\s*(\d+)[.)]\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex quote = new(
        @"^\s*>\s?(.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex fence = new(
        @"^\s{0,3}(`{3,}|~{3,})\s*([^\s]*)?.*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex divider = new(
        @"^\s*(?:(?:-\s*){3,}|(?:_\s*){3,}|(?:\*\s*){3,})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex localLink = new(
        @"(?<!!)\[([^\]]+)]\(([^)]+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex anyLink = new(
        @"\[([^\]]+)]\(([^)]+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool LooksLikeQuestion(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.EndsWith("?", StringComparison.Ordinal) ||
            text.EndsWith("？", StringComparison.Ordinal) ||
            Regex.IsMatch(
                text,
                "(?:请|需要你|麻烦你).{0,12}(?:提供|确认|选择|补充|回复|告诉)",
                RegexOptions.CultureInvariant);
    }

    public static IReadOnlyList<string> SplitMessage(
        string? value,
        string fallback)
    {
        var normalized = NormalizeMarkdown(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = fallback.Trim();
        }
        if (normalized.Length == 0)
        {
            return [fallback];
        }

        var chunks = new List<string>((normalized.Length / MessageChunkLength) + 1);
        var remaining = normalized;
        while (remaining.Length > MessageChunkLength)
        {
            var splitAt = PreferredSplitIndex(remaining, MessageChunkLength);
            chunks.Add(remaining[..splitAt]);
            remaining = remaining[splitAt..];
        }
        if (remaining.Length > 0)
        {
            chunks.Add(remaining);
        }
        return chunks;
    }

    public static IReadOnlyList<JsonNode?> ToElements(string value)
    {
        var normalized = NormalizeMarkdown(value);
        if (normalized.Length == 0)
        {
            return [];
        }

        var lines = normalized.Split('\n');
        var elements = new List<JsonNode?>();
        var index = 0;
        var nativeTables = 0;
        while (index < lines.Length)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                index++;
                continue;
            }

            var fenceMatch = fence.Match(line);
            if (fenceMatch.Success)
            {
                var marker = fenceMatch.Groups[1].Value;
                var language = fenceMatch.Groups[2].Value.Trim();
                var code = new List<string>();
                index++;
                while (index < lines.Length && !ClosesFence(lines[index], marker))
                {
                    code.Add(lines[index]);
                    index++;
                }
                if (index < lines.Length)
                {
                    index++;
                }
                AddLongPlain(
                    elements,
                    $"代码{(language.Length > 0 ? $" · {ToPlainText(language)}" : string.Empty)}\n" +
                    (code.Count == 0 ? "（空代码块）" : string.Join('\n', code).TrimEnd()));
                continue;
            }

            var headingMatch = heading.Match(line);
            if (headingMatch.Success)
            {
                elements.Add(MarkdownDiv($"**{NormalizeInline(headingMatch.Groups[2].Value)}**"));
                index++;
                continue;
            }

            if (divider.IsMatch(line))
            {
                elements.Add(new JsonObject { ["tag"] = "hr" });
                index++;
                continue;
            }

            if (IsTableStart(lines, index))
            {
                var rows = new List<string[]> { ParseTableRow(line) };
                index += 2;
                while (index < lines.Length && IsTableRow(lines[index]))
                {
                    rows.Add(ParseTableRow(lines[index]));
                    index++;
                }
                var tableElements = TableElements(rows);
                if (nativeTables < MaximumNativeTables &&
                    nativeTables + tableElements.Count <= MaximumNativeTables)
                {
                    elements.AddRange(tableElements);
                    nativeTables += tableElements.Count;
                }
                else
                {
                    AddLongMarkdown(elements, TableFallback(rows));
                }
                continue;
            }

            var quoteMatch = quote.Match(line);
            if (quoteMatch.Success)
            {
                var quoted = new List<string>();
                while (index < lines.Length && quote.Match(lines[index]).Success)
                {
                    quoted.Add(quote.Match(lines[index]).Groups[1].Value);
                    index++;
                }
                elements.Add(Note(ToPlainText(string.Join('\n', quoted).Trim())));
                continue;
            }

            var unorderedMatch = unordered.Match(line);
            if (unorderedMatch.Success)
            {
                var items = new List<string>();
                while (index < lines.Length)
                {
                    var match = unordered.Match(lines[index]);
                    if (!match.Success || divider.IsMatch(lines[index]))
                    {
                        break;
                    }
                    items.Add($"• {NormalizeListItem(match.Groups[1].Value)}");
                    index++;
                }
                AddLongMarkdown(elements, string.Join('\n', items));
                continue;
            }

            var orderedMatch = ordered.Match(line);
            if (orderedMatch.Success)
            {
                var items = new List<string>();
                while (index < lines.Length)
                {
                    var match = ordered.Match(lines[index]);
                    if (!match.Success)
                    {
                        break;
                    }
                    items.Add($"{match.Groups[1].Value}．{NormalizeListItem(match.Groups[2].Value)}");
                    index++;
                }
                AddLongMarkdown(elements, string.Join('\n', items));
                continue;
            }

            var paragraph = new List<string> { line.Trim() };
            index++;
            while (index < lines.Length &&
                !string.IsNullOrWhiteSpace(lines[index]) &&
                !StartsBlock(lines, index))
            {
                paragraph.Add(lines[index].Trim());
                index++;
            }
            AddLongMarkdown(elements, NormalizeInline(string.Join('\n', paragraph)));
        }
        return elements;
    }

    private static int PreferredSplitIndex(string value, int limit)
    {
        var minimum = (int)Math.Floor(limit * 0.55);
        var window = value[..Math.Min(limit, value.Length)];
        foreach (var separator in new[] { "\n\n", "\n", "。", "！", "？", ". ", "; ", "，", ", ", " " })
        {
            var index = window.LastIndexOf(separator, StringComparison.Ordinal);
            if (index >= minimum)
            {
                return index + separator.Length;
            }
        }
        return limit;
    }

    private static string NormalizeMarkdown(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        var normalized = ansi.Replace(value, string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n')
            .Select(line => line.TrimEnd(' ', '\t'))
            .ToArray();
        normalized = string.Join('\n', lines);
        while (normalized.Contains("\n\n\n\n", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("\n\n\n\n", "\n\n\n", StringComparison.Ordinal);
        }
        return normalized.Trim();
    }

    private static bool StartsBlock(string[] lines, int index) =>
        fence.IsMatch(lines[index]) ||
        heading.IsMatch(lines[index]) ||
        quote.IsMatch(lines[index]) ||
        unordered.IsMatch(lines[index]) ||
        ordered.IsMatch(lines[index]) ||
        divider.IsMatch(lines[index]) ||
        IsTableStart(lines, index);

    private static bool ClosesFence(string line, string marker)
    {
        var value = line.Trim();
        return value.Length >= marker.Length &&
            value.All(character => character == marker[0]);
    }

    private static bool IsTableStart(string[] lines, int index) =>
        index + 1 < lines.Length &&
        IsTableRow(lines[index]) &&
        IsTableSeparator(lines[index + 1]);

    private static bool IsTableRow(string line) =>
        line.Contains('|', StringComparison.Ordinal) && ParseTableRow(line).Length >= 2;

    private static bool IsTableSeparator(string line)
    {
        var cells = ParseTableRow(line);
        return cells.Length >= 2 && cells.All(cell =>
            Regex.IsMatch(cell, @"^:?-{3,}:?$", RegexOptions.CultureInvariant));
    }

    private static string[] ParseTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }
        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }
        var cells = new List<string>();
        var current = new StringBuilder();
        var escaped = false;
        foreach (var character in trimmed)
        {
            if (escaped)
            {
                current.Append(character);
                escaped = false;
            }
            else if (character == '\\')
            {
                escaped = true;
            }
            else if (character == '|')
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }
        if (escaped)
        {
            current.Append('\\');
        }
        cells.Add(current.ToString().Trim());
        return cells.ToArray();
    }

    private static IReadOnlyList<JsonNode?> TableElements(IReadOnlyList<string[]> rows)
    {
        var columnCount = rows.Count == 0 ? 0 : rows.Max(row => row.Length);
        if (columnCount == 0)
        {
            return [];
        }
        var header = rows[0];
        var body = rows.Skip(1).ToArray();
        var result = new List<JsonNode?>();
        for (var start = 0; start < columnCount && result.Count < MaximumNativeTables; start += MaximumTableColumns)
        {
            var end = Math.Min(start + MaximumTableColumns, columnCount);
            var columns = new JsonArray();
            for (var column = start; column < end; column++)
            {
                columns.Add(new JsonObject
                {
                    ["name"] = $"column_{column + 1}",
                    ["display_name"] = ToPlainText(
                        column < header.Length ? header[column] : string.Empty) is { Length: > 0 } name
                        ? name
                        : $"第 {column + 1} 列",
                    ["data_type"] = "text",
                    ["width"] = "auto",
                });
            }
            var rowValues = new JsonArray();
            foreach (var row in body)
            {
                var record = new JsonObject();
                for (var column = start; column < end; column++)
                {
                    record[$"column_{column + 1}"] = ToPlainText(
                        column < row.Length ? row[column] : string.Empty);
                }
                rowValues.Add(record);
            }
            result.Add(new JsonObject
            {
                ["tag"] = "table",
                ["page_size"] = Math.Min(10, Math.Max(1, body.Length)),
                ["row_height"] = "high",
                ["header_style"] = new JsonObject
                {
                    ["text_align"] = "left",
                    ["text_size"] = "normal",
                    ["background_style"] = "grey",
                    ["text_color"] = "default",
                    ["bold"] = true,
                },
                ["columns"] = columns,
                ["rows"] = rowValues,
            });
        }
        return result;
    }

    private static string TableFallback(IReadOnlyList<string[]> rows)
    {
        var header = rows.Count > 0 ? rows[0] : [];
        var body = rows.Skip(1).ToArray();
        if (body.Length == 0)
        {
            return string.Join(" · ", header.Select(ToPlainText).Where(value => value.Length > 0));
        }
        return string.Join(
            "\n\n",
            body.Select(row => string.Join(
                "\n",
                row.Select((cell, index) =>
                    $"**{(index < header.Length ? ToPlainText(header[index]) : $"第 {index + 1} 列")}：** {NormalizeInline(cell)}"))));
    }

    private static void AddLongMarkdown(List<JsonNode?> elements, string value)
    {
        foreach (var chunk in SplitLongText(value))
        {
            if (chunk.Length > 0)
            {
                elements.Add(MarkdownDiv(chunk));
            }
        }
    }

    private static void AddLongPlain(List<JsonNode?> elements, string value)
    {
        foreach (var chunk in SplitLongText(value))
        {
            if (chunk.Length > 0)
            {
                elements.Add(PlainDiv(chunk));
            }
        }
    }

    private static IReadOnlyList<string> SplitLongText(string value, int limit = 1_500)
    {
        if (value.Length <= limit)
        {
            return [value];
        }
        var chunks = new List<string>();
        var remaining = value;
        while (remaining.Length > limit)
        {
            var newline = remaining[..limit].LastIndexOf('\n');
            var splitAt = newline >= Math.Floor(limit * 0.6) ? newline : limit;
            chunks.Add(remaining[..splitAt]);
            remaining = remaining[splitAt..];
        }
        if (remaining.Length > 0)
        {
            chunks.Add(remaining);
        }
        return chunks;
    }

    private static string NormalizeInline(string value)
    {
        var normalized = value
            .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase);
        normalized = Regex.Replace(
            normalized,
            @"<at\b[^>]*>[\s\S]*?</at>",
            "@提及",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"<[^>\n]+>",
            string.Empty,
            RegexOptions.CultureInvariant);
        normalized = localLink.Replace(normalized, match =>
        {
            var target = match.Groups[2].Value.Trim();
            return IsLocalLink(target)
                ? $"{match.Groups[1].Value}（{target}）"
                : match.Value;
        });
        normalized = Regex.Replace(
            normalized,
            @"!\[([^\]]*)]\(([^)]+)\)",
            match => $"[图片：{(match.Groups[1].Value.Length > 0 ? match.Groups[1].Value : "未命名")}]({match.Groups[2].Value})",
            RegexOptions.CultureInvariant);
        return normalized.Trim();
    }

    private static string NormalizeListItem(string value)
    {
        var match = Regex.Match(value, @"^\[([ xX])\]\s*(.*)$", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return NormalizeInline(value);
        }
        return $"{(char.ToLowerInvariant(match.Groups[1].Value[0]) == 'x' ? '☑' : '☐')} " +
            NormalizeInline(match.Groups[2].Value);
    }

    private static string ToPlainText(string value)
    {
        var normalized = NormalizeInline(value);
        normalized = anyLink.Replace(normalized, "$1（$2）");
        normalized = Regex.Replace(normalized, @"\*\*([^*]+)\*\*", "$1");
        normalized = Regex.Replace(normalized, @"__([^_]+)__", "$1");
        normalized = Regex.Replace(normalized, @"~~([^~]+)~~", "$1");
        normalized = Regex.Replace(normalized, @"`([^`]+)`", "$1");
        normalized = Regex.Replace(normalized, @"\*([^*]+)\*", "$1");
        normalized = Regex.Replace(normalized, @"_([^_]+)_", "$1");
        return normalized.Trim();
    }

    private static bool IsLocalLink(string value)
    {
        var target = value.Trim();
        return Regex.IsMatch(target, @"^[a-zA-Z]:[\\/]", RegexOptions.CultureInvariant) ||
            target.StartsWith("/", StringComparison.Ordinal) ||
            target.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject MarkdownDiv(string content) => new()
    {
        ["tag"] = "div",
        ["text"] = new JsonObject
        {
            ["tag"] = "lark_md",
            ["content"] = content,
        },
    };

    private static JsonObject PlainDiv(string content) => new()
    {
        ["tag"] = "div",
        ["text"] = new JsonObject
        {
            ["tag"] = "plain_text",
            ["content"] = content,
        },
    };

    private static JsonObject Note(string content) => new()
    {
        ["tag"] = "note",
        ["elements"] = new JsonArray(new JsonObject
        {
            ["tag"] = "plain_text",
            ["content"] = content,
        }),
    };
}
