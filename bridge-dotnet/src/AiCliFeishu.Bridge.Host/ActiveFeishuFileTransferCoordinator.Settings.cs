using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishu.Bridge.Host;

internal sealed record ActiveFeishuFileTransferSettings(
    string UploadsDirectory,
    long InboundFileMaxBytes,
    int InboundAttachmentMaxCount,
    int UploadMaxFiles,
    long UploadMaxBytes,
    TimeSpan UploadTtl,
    long OutboundFileMaxBytes)
{
    private const long DefaultInboundFileMaxBytes = 25 * 1024 * 1024;
    private const int DefaultInboundAttachmentMaxCount = 4;
    private const int DefaultUploadMaxFiles = 500;
    private const long DefaultUploadMaxBytes = 1024L * 1024 * 1024;
    private const long DefaultUploadTtlMilliseconds = 7L * 24 * 60 * 60 * 1000;
    private const long DefaultOutboundFileMaxBytes = 30 * 1024 * 1024;
    private static readonly string[] VariableNames =
    [
        "FEISHU_INBOUND_FILE_MAX_BYTES",
        "FEISHU_INBOUND_ATTACHMENT_MAX_COUNT",
        "FEISHU_UPLOAD_MAX_FILES",
        "FEISHU_UPLOAD_MAX_BYTES",
        "FEISHU_UPLOAD_TTL_MS",
        "FEISHU_OUTBOUND_FILE_MAX_BYTES",
    ];

    public static ActiveFeishuFileTransferSettings Load(BridgeHostOptions options) =>
        Load(
            options,
            Environment.GetEnvironmentVariable,
            path => File.Exists(path) ? File.ReadAllText(path) : null);

    internal static ActiveFeishuFileTransferSettings Load(
        BridgeHostOptions options,
        Func<string, string?> readEnvironment,
        Func<string, string?> readFile)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(readEnvironment);
        ArgumentNullException.ThrowIfNull(readFile);
        var dataDirectory = Path.GetFullPath(options.DataDirectory);
        var configurationDirectory = Path.GetDirectoryName(dataDirectory) ?? dataDirectory;
        var fileValues = ParseEnvironmentFile(
            readFile(Path.Combine(configurationDirectory, ".env")));
        string? Value(string name) =>
            readEnvironment(name) ?? fileValues.GetValueOrDefault(name);

        var ttlMilliseconds = PositiveInt64(
            Value("FEISHU_UPLOAD_TTL_MS"),
            DefaultUploadTtlMilliseconds);
        return new(
            Path.Combine(dataDirectory, "uploads"),
            PositiveInt64(
                Value("FEISHU_INBOUND_FILE_MAX_BYTES"),
                DefaultInboundFileMaxBytes),
            PositiveInt32(
                Value("FEISHU_INBOUND_ATTACHMENT_MAX_COUNT"),
                DefaultInboundAttachmentMaxCount),
            PositiveInt32(
                Value("FEISHU_UPLOAD_MAX_FILES"),
                DefaultUploadMaxFiles),
            PositiveInt64(
                Value("FEISHU_UPLOAD_MAX_BYTES"),
                DefaultUploadMaxBytes),
            TimeSpan.FromMilliseconds(Math.Min(
                ttlMilliseconds,
                (long)TimeSpan.MaxValue.TotalMilliseconds)),
            PositiveInt64(
                Value("FEISHU_OUTBOUND_FILE_MAX_BYTES"),
                DefaultOutboundFileMaxBytes));
    }

    private static int PositiveInt32(string? value, int fallback) =>
        long.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
        parsed is > 0 and <= int.MaxValue
            ? (int)parsed
            : fallback;

    private static long PositiveInt64(string? value, long fallback) =>
        long.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
        parsed > 0
            ? parsed
            : fallback;

    private static IReadOnlyDictionary<string, string> ParseEnvironmentFile(string? content)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (content is null)
        {
            return values;
        }
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }
            if (trimmed.StartsWith("export ", StringComparison.Ordinal))
            {
                trimmed = trimmed[7..].TrimStart();
            }
            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }
            var key = trimmed[..separator].Trim();
            if (!VariableNames.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }
            values[key] = ParseEnvironmentValue(trimmed[(separator + 1)..]);
        }
        return values;
    }

    private static string ParseEnvironmentValue(string source)
    {
        var value = source.Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }
        if (value[0] is not ('\'' or '"' or '`'))
        {
            var comment = value.IndexOf('#');
            return (comment >= 0 ? value[..comment] : value).Trim();
        }
        var quote = value[0];
        var closing = value.LastIndexOf(quote);
        return closing > 0 ? value[1..closing] : value;
    }
}
