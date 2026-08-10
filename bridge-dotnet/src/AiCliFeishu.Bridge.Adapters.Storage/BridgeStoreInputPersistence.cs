using System.Globalization;
using System.Text.Json;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Adapters.Storage;

internal static class BridgeStoreInputPersistence
{
    internal const string ExtensionPropertyName = "pendingInputs";

    public static Dictionary<string, JsonElement>? MergeExtensionData(
        Dictionary<string, JsonElement>? existing,
        InputRegistryState inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var extensions = existing?.ToDictionary(
            item => item.Key,
            item => item.Value.Clone(),
            StringComparer.Ordinal) ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var key in extensions.Keys.Where(key => string.Equals(
                     key,
                     ExtensionPropertyName,
                     StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            extensions.Remove(key);
        }

        var pending = inputs.Requests
            .Where(item => item.Value.Status == InputRequestStatuses.Pending)
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                item => item.Key,
                item => ToStoreRecord(item.Value),
                StringComparer.Ordinal);
        if (pending.Count > 0)
        {
            extensions[ExtensionPropertyName] = JsonSerializer.SerializeToElement(
                pending,
                BridgeStoreJson.Options);
        }
        return extensions.Count == 0 ? null : extensions;
    }

    public static InputRegistryState Project(SessionStoreDocument sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var matches = sessions.ExtensionData?
            .Where(item => string.Equals(
                item.Key,
                ExtensionPropertyName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray() ?? [];
        if (matches.Length == 0)
        {
            return InputRegistryState.Empty;
        }
        if (matches.Length != 1)
        {
            throw new BridgeStoreValidationException(
                BridgeStoreFile.Sessions.FileName,
                [$"{ExtensionPropertyName} 扩展字段不能重复"]);
        }

        Dictionary<string, PendingInputStoreRecord> records;
        try
        {
            records = JsonSerializer.Deserialize<Dictionary<string, PendingInputStoreRecord>>(
                matches[0].Value.GetRawText(),
                BridgeStoreJson.Options) ?? throw new InvalidDataException(
                    $"{ExtensionPropertyName} 不能为空。");
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            throw new BridgeStoreValidationException(
                BridgeStoreFile.Sessions.FileName,
                [$"{ExtensionPropertyName} 无法还原：{error.Message}"]);
        }

        var requests = records
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                item => item.Key,
                item => ToCoreState(item.Value),
                StringComparer.Ordinal);
        return new InputRegistryState(requests);
    }

    private static PendingInputStoreRecord ToStoreRecord(InputRequestState request)
    {
        var secretQuestions = request.Questions
            .Where(question => question.IsSecret)
            .Select(question => question.Id)
            .ToHashSet(StringComparer.Ordinal);
        return new PendingInputStoreRecord
        {
            RequestId = request.RequestId,
            SessionId = request.SessionId,
            Status = request.Status,
            CreatedAt = Timestamp(request.CreatedAt),
            ExpiresAt = Timestamp(request.ExpiresAt),
            Questions = request.Questions.Select(question => new PendingInputQuestionStoreRecord
            {
                Id = question.Id,
                Header = question.Header,
                Prompt = question.Prompt,
                Multiple = question.Multiple,
                AllowsCustom = question.AllowsCustom,
                Options = question.Options.ToList(),
                IsSecret = question.IsSecret,
            }).ToList(),
            Answers = request.Answers
                .Where(item => !secretQuestions.Contains(item.Key))
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(
                    item => item.Key,
                    item => item.Value.ToList(),
                    StringComparer.Ordinal),
        };
    }

    private static InputRequestState ToCoreState(PendingInputStoreRecord record) => new(
        record.RequestId,
        record.SessionId,
        record.Status,
        ParseTimestamp(record.CreatedAt),
        ParseTimestamp(record.ExpiresAt),
        record.Questions.Select(question => new InputQuestionState(
            question.Id,
            question.Multiple,
            question.AllowsCustom,
            question.Options.ToArray(),
            question.Header,
            question.Prompt,
            question.IsSecret)).ToArray(),
        record.Answers.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<string>)item.Value.ToArray(),
            StringComparer.Ordinal));

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    internal sealed class PendingInputStoreRecord : ExtensibleStoreObject
    {
        public string RequestId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
        public string ExpiresAt { get; set; } = string.Empty;
        public List<PendingInputQuestionStoreRecord> Questions { get; set; } = [];
        public Dictionary<string, List<string>> Answers { get; set; } = [];
    }

    internal sealed class PendingInputQuestionStoreRecord : ExtensibleStoreObject
    {
        public string Id { get; set; } = string.Empty;
        public string? Header { get; set; }
        public string? Prompt { get; set; }
        public bool Multiple { get; set; }
        public bool AllowsCustom { get; set; }
        public List<string> Options { get; set; } = [];
        public bool IsSecret { get; set; }
    }
}
