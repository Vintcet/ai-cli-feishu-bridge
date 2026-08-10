namespace AiCliFeishuControl;

internal static class BridgeEnvironmentReader
{
    public static string? Read(string bridgeRoot, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var environment = Environment.GetEnvironmentVariable(name)?.Trim();
        if (!string.IsNullOrEmpty(environment))
        {
            return environment;
        }

        try
        {
            var path = Path.Combine(bridgeRoot, ".env");
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
        return closing > 0 ? value[1..closing] : string.Empty;
    }
}
