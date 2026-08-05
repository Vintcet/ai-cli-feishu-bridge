using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiCliFeishu.Bridge.Replay;

public sealed record BehaviorReplayDifference(
    int LineNumber,
    string RecordId,
    string Path,
    string Message);

public sealed record BehaviorReplayResult(
    int Total,
    int Matched,
    int Mismatched,
    int Invalid,
    IReadOnlyList<BehaviorReplayDifference> Differences)
{
    public bool IsSuccess => Total > 0 && Mismatched == 0 && Invalid == 0;
}

public sealed class BehaviorReplayEngine
{
    private static readonly IReadOnlySet<string> Stages = Set(
        "ingress.hook",
        "ingress.opencode",
        "ingress.feishu",
        "core.state_committed",
        "core.decision",
        "egress.runtime_command",
        "egress.feishu");

    private static readonly IReadOnlySet<string> Outcomes = Set(
        "observed",
        "succeeded",
        "failed");

    private static readonly IReadOnlySet<string> Runtimes = Set(
        "codex",
        "claudecode",
        "opencode");

    public BehaviorReplayResult ReplayFile(string filePath)
    {
        using var reader = new StreamReader(
            File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        return Replay(reader);
    }

    public BehaviorReplayResult Replay(TextReader reader)
    {
        var total = 0;
        var matched = 0;
        var mismatched = 0;
        var invalid = 0;
        var lineNumber = 0;
        var differences = new List<BehaviorReplayDifference>();

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber += 1;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            total += 1;
            try
            {
                using var document = JsonDocument.Parse(line);
                var record = document.RootElement;
                ValidateRecord(record);
                var recordId = RequiredString(record, "recordId");
                var expected = record.GetProperty("expectedProjection");
                var actual = BuildProjection(record);
                var itemDifferences = new List<BehaviorReplayDifference>();
                Compare(
                    expected,
                    actual,
                    "$",
                    lineNumber,
                    recordId,
                    itemDifferences);
                if (itemDifferences.Count == 0)
                {
                    matched += 1;
                }
                else
                {
                    mismatched += 1;
                    differences.AddRange(itemDifferences);
                }
            }
            catch (Exception error) when (
                error is JsonException or InvalidDataException)
            {
                invalid += 1;
                differences.Add(new BehaviorReplayDifference(
                    lineNumber,
                    TryReadRecordId(line),
                    "$",
                    error.Message));
            }
        }

        return new BehaviorReplayResult(
            total,
            matched,
            mismatched,
            invalid,
            differences);
    }

    public JsonElement BuildProjection(JsonElement record)
    {
        ValidateRecord(record);
        var projection = new JsonObject
        {
            ["stage"] = RequiredString(record, "stage"),
            ["kind"] = RequiredString(record, "kind"),
        };
        if (record.TryGetProperty("runtime", out var runtime))
        {
            projection["runtime"] = runtime.GetString();
        }
        projection["outcome"] = RequiredString(record, "outcome");
        projection["observed"] = JsonNode.Parse(record.GetProperty("observed").GetRawText());
        return JsonSerializer.SerializeToElement(projection);
    }

    private static void ValidateRecord(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Behavior record must be an object.");
        }
        if (!record.TryGetProperty("recordVersion", out var version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var number) ||
            number != 1)
        {
            throw new InvalidDataException("recordVersion must be 1.");
        }
        if (RequiredString(record, "source") != "node")
        {
            throw new InvalidDataException("source must be node.");
        }
        RequiredString(record, "recordId");
        RequiredString(record, "recordedAt");
        RequiredString(record, "traceId");
        RequiredMember(record, "observed");
        var stage = RequiredString(record, "stage");
        if (!Stages.Contains(stage))
        {
            throw new InvalidDataException($"Unsupported stage: {stage}.");
        }
        RequiredString(record, "kind");
        var outcome = RequiredString(record, "outcome");
        if (!Outcomes.Contains(outcome))
        {
            throw new InvalidDataException($"Unsupported outcome: {outcome}.");
        }
        if (record.TryGetProperty("runtime", out var runtime))
        {
            var runtimeName = runtime.ValueKind == JsonValueKind.String
                ? runtime.GetString()
                : null;
            if (runtimeName is null || !Runtimes.Contains(runtimeName))
            {
                throw new InvalidDataException("runtime is not supported.");
            }
        }
        var expected = RequiredMember(record, "expectedProjection");
        if (expected.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("expectedProjection must be an object.");
        }
    }

    private static JsonElement RequiredMember(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var member))
        {
            throw new InvalidDataException($"Missing required property: {name}.");
        }
        return member;
    }

    private static string RequiredString(JsonElement value, string name)
    {
        var member = RequiredMember(value, name);
        var text = member.ValueKind == JsonValueKind.String
            ? member.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException($"{name} must be a non-empty string.");
        }
        return text;
    }

    private static void Compare(
        JsonElement expected,
        JsonElement actual,
        string path,
        int lineNumber,
        string recordId,
        List<BehaviorReplayDifference> differences)
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            AddDifference(
                differences,
                lineNumber,
                recordId,
                path,
                $"expected {expected.ValueKind}, actual {actual.ValueKind}");
            return;
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                CompareObjects(expected, actual, path, lineNumber, recordId, differences);
                return;
            case JsonValueKind.Array:
                CompareArrays(expected, actual, path, lineNumber, recordId, differences);
                return;
            case JsonValueKind.Number:
                if (!NumbersEqual(expected, actual))
                {
                    AddDifference(
                        differences,
                        lineNumber,
                        recordId,
                        path,
                        $"expected {expected.GetRawText()}, actual {actual.GetRawText()}");
                }
                return;
            case JsonValueKind.String:
                if (!string.Equals(expected.GetString(), actual.GetString(), StringComparison.Ordinal))
                {
                    AddDifference(
                        differences,
                        lineNumber,
                        recordId,
                        path,
                        $"expected {JsonSerializer.Serialize(expected.GetString())}, actual {JsonSerializer.Serialize(actual.GetString())}");
                }
                return;
            case JsonValueKind.True:
            case JsonValueKind.False:
                if (expected.GetBoolean() != actual.GetBoolean())
                {
                    AddDifference(
                        differences,
                        lineNumber,
                        recordId,
                        path,
                        $"expected {expected.GetBoolean().ToString().ToLowerInvariant()}, actual {actual.GetBoolean().ToString().ToLowerInvariant()}");
                }
                return;
            case JsonValueKind.Null:
                return;
            default:
                if (expected.GetRawText() != actual.GetRawText())
                {
                    AddDifference(
                        differences,
                        lineNumber,
                        recordId,
                        path,
                        $"expected {expected.GetRawText()}, actual {actual.GetRawText()}");
                }
                return;
        }
    }

    private static void CompareObjects(
        JsonElement expected,
        JsonElement actual,
        string path,
        int lineNumber,
        string recordId,
        List<BehaviorReplayDifference> differences)
    {
        var expectedProperties = expected.EnumerateObject()
            .ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);
        var actualProperties = actual.EnumerateObject()
            .ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);
        foreach (var name in expectedProperties.Keys.Union(actualProperties.Keys).Order())
        {
            var childPath = $"{path}.{name}";
            if (!expectedProperties.TryGetValue(name, out var expectedValue))
            {
                AddDifference(
                    differences,
                    lineNumber,
                    recordId,
                    childPath,
                    "unexpected property in actual projection");
                continue;
            }
            if (!actualProperties.TryGetValue(name, out var actualValue))
            {
                AddDifference(
                    differences,
                    lineNumber,
                    recordId,
                    childPath,
                    "property is missing from actual projection");
                continue;
            }
            Compare(
                expectedValue,
                actualValue,
                childPath,
                lineNumber,
                recordId,
                differences);
        }
    }

    private static void CompareArrays(
        JsonElement expected,
        JsonElement actual,
        string path,
        int lineNumber,
        string recordId,
        List<BehaviorReplayDifference> differences)
    {
        var expectedItems = expected.EnumerateArray().ToArray();
        var actualItems = actual.EnumerateArray().ToArray();
        if (expectedItems.Length != actualItems.Length)
        {
            AddDifference(
                differences,
                lineNumber,
                recordId,
                path,
                $"expected {expectedItems.Length} items, actual {actualItems.Length}");
        }
        for (var index = 0; index < Math.Min(expectedItems.Length, actualItems.Length); index += 1)
        {
            Compare(
                expectedItems[index],
                actualItems[index],
                $"{path}[{index}]",
                lineNumber,
                recordId,
                differences);
        }
    }

    private static bool NumbersEqual(JsonElement left, JsonElement right)
    {
        if (left.TryGetDecimal(out var leftDecimal) && right.TryGetDecimal(out var rightDecimal))
        {
            return leftDecimal == rightDecimal;
        }
        return double.TryParse(left.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var leftDouble) &&
            double.TryParse(right.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rightDouble) &&
            leftDouble.Equals(rightDouble);
    }

    private static void AddDifference(
        List<BehaviorReplayDifference> differences,
        int lineNumber,
        string recordId,
        string path,
        string message)
    {
        differences.Add(new BehaviorReplayDifference(
            lineNumber,
            recordId,
            path,
            message));
    }

    private static string TryReadRecordId(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.TryGetProperty("recordId", out var recordId) &&
                    recordId.ValueKind == JsonValueKind.String
                ? recordId.GetString() ?? "(unknown)"
                : "(unknown)";
        }
        catch (JsonException)
        {
            return "(unknown)";
        }
    }

    private static IReadOnlySet<string> Set(params string[] values)
    {
        return new HashSet<string>(values, StringComparer.Ordinal);
    }
}
