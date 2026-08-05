using System.Text.Json;

namespace AiCliFeishu.Bridge.Adapters.OpenCode;

public sealed record OpenCodeRawEvent(string Type, JsonElement Properties);

public sealed class OpenCodeSseParser
{
    private string buffer = string.Empty;

    public IReadOnlyList<OpenCodeRawEvent> Feed(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return [];
        }
        buffer = NormalizeLineEndings(buffer + chunk, final: false);
        var events = new List<OpenCodeRawEvent>();
        var boundary = buffer.IndexOf("\n\n", StringComparison.Ordinal);
        while (boundary >= 0)
        {
            var frame = buffer[..boundary];
            buffer = buffer[(boundary + 2)..];
            var parsed = ParseFrame(frame);
            if (parsed is not null)
            {
                events.Add(parsed);
            }
            boundary = buffer.IndexOf("\n\n", StringComparison.Ordinal);
        }
        return events;
    }

    public IReadOnlyList<OpenCodeRawEvent> Complete()
    {
        buffer = NormalizeLineEndings(buffer, final: true);
        if (string.IsNullOrWhiteSpace(buffer))
        {
            buffer = string.Empty;
            return [];
        }
        var parsed = ParseFrame(buffer);
        buffer = string.Empty;
        return parsed is null ? [] : [parsed];
    }

    public void Reset() => buffer = string.Empty;

    private static string NormalizeLineEndings(string value, bool final)
    {
        var trailingCarriageReturn = !final && value.EndsWith('\r');
        var content = trailingCarriageReturn ? value[..^1] : value;
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return trailingCarriageReturn ? normalized + '\r' : normalized;
    }

    private static OpenCodeRawEvent? ParseFrame(string frame)
    {
        string? eventType = null;
        var dataLines = new List<string>();
        foreach (var line in frame.Split('\n'))
        {
            if (line.StartsWith(':'))
            {
                continue;
            }
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventType = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                dataLines.Add(line["data:".Length..].TrimStart());
            }
        }

        JsonElement properties = JsonSerializer.SerializeToElement(new { });
        if (dataLines.Count > 0)
        {
            try
            {
                using var document = JsonDocument.Parse(string.Join("\n", dataLines));
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("type", out var type) &&
                        type.ValueKind == JsonValueKind.String)
                    {
                        eventType = type.GetString();
                        properties = root.TryGetProperty("properties", out var nested) &&
                            nested.ValueKind == JsonValueKind.Object
                                ? nested.Clone()
                                : JsonSerializer.SerializeToElement(new { });
                    }
                    else
                    {
                        properties = root.Clone();
                    }
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return string.IsNullOrWhiteSpace(eventType)
            ? null
            : new OpenCodeRawEvent(eventType, properties);
    }
}
