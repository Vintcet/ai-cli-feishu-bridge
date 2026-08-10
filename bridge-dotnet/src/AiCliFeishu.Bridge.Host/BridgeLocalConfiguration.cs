namespace AiCliFeishu.Bridge.Host;

internal static class BridgeLocalConfiguration
{
    private static readonly TimeSpan DefaultSessionActiveLifetime = TimeSpan.FromDays(1);

    public static void LoadIntoProcessEnvironment(BridgeHostOptions options)
    {
        try
        {
            var path = Path.Combine(BridgeRoot(options), ".env");
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                if (trimmed.StartsWith("export ", StringComparison.Ordinal)) trimmed = trimmed[7..].TrimStart();
                var separator = trimmed.IndexOf('=');
                if (separator <= 0) continue;
                var key = trimmed[..separator].Trim();
                if (Environment.GetEnvironmentVariable(key) is null)
                    Environment.SetEnvironmentVariable(key, ParseValue(trimmed[(separator + 1)..]));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static string? Read(BridgeHostOptions options, string name)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var environment = Environment.GetEnvironmentVariable(name)?.Trim();
        if (!string.IsNullOrEmpty(environment))
        {
            return environment;
        }

        try
        {
            var path = Path.Combine(BridgeRoot(options), ".env");
            if (!File.Exists(path))
            {
                return null;
            }
            foreach (var line in File.ReadLines(path))
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
                if (separator <= 0 ||
                    !string.Equals(
                        trimmed[..separator].Trim(),
                        name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                return ParseValue(trimmed[(separator + 1)..]);
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        return null;
    }

    public static string BridgeRoot(BridgeHostOptions options)
    {
        var dataDirectory = Path.GetFullPath(options.DataDirectory);
        return Path.GetDirectoryName(dataDirectory) ?? dataDirectory;
    }

    public static TimeSpan SessionActiveLifetime(BridgeHostOptions options) =>
        ParsePositiveMilliseconds(
            Read(options, "CODEX_SESSION_ACTIVE_MS"),
            DefaultSessionActiveLifetime);

    internal static TimeSpan ParsePositiveMilliseconds(string? value, TimeSpan fallback)
    {
        if (long.TryParse(
                value,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var milliseconds) &&
            milliseconds > 0 &&
            milliseconds <= TimeSpan.MaxValue.TotalMilliseconds)
        {
            return TimeSpan.FromMilliseconds(milliseconds);
        }
        return fallback;
    }

    private static string ParseValue(string source)
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
        if (closing <= 0)
        {
            return string.Empty;
        }
        return value[1..closing];
    }
}
