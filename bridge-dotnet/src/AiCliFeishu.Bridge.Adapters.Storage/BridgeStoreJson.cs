using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiCliFeishu.Bridge.Adapters.Storage;

public static class BridgeStoreJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static T Deserialize<T>(string json, BridgeStoreFile file)
        where T : class
    {
        using var document = JsonDocument.Parse(json);
        BridgeStoreValidator.Validate(file, document.RootElement);
        return JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new InvalidDataException($"{file.FileName} 不能反序列化。 ");
    }

    public static string Serialize<T>(T value) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.Serialize(value, Options) + "\n";
    }
}
