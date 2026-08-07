namespace AiCliFeishu.Bridge.Host;

internal sealed class BridgeFeishuCredentials
{
    public BridgeFeishuCredentials(string appId, string appSecret)
    {
        AppId = appId;
        AppSecret = appSecret;
    }

    internal string AppId { get; }

    internal string AppSecret { get; }

    public override string ToString() => "Feishu credentials (redacted)";
}

internal sealed class ActiveFeishuCredentialSource :
    IBridgeFeishuCredentialSource,
    IBridgeHostSubsystem,
    IBridgeHostSubsystemHealth
{
    private const string AppIdVariable = "FEISHU_APP_ID";
    private const string AppSecretVariable = "FEISHU_APP_SECRET";

    private readonly BridgeHostOptions options;
    private readonly Func<string, string?> readEnvironment;
    private readonly Func<string, string?> readFile;
    private readonly Lazy<BridgeFeishuCredentials> credentials;
    private int started;

    public ActiveFeishuCredentialSource(BridgeHostOptions options)
        : this(
            options,
            Environment.GetEnvironmentVariable,
            path => File.Exists(path) ? File.ReadAllText(path) : null)
    {
    }

    internal ActiveFeishuCredentialSource(
        BridgeHostOptions options,
        Func<string, string?> readEnvironment,
        Func<string, string?> readFile)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.readEnvironment = readEnvironment ??
            throw new ArgumentNullException(nameof(readEnvironment));
        this.readFile = readFile ?? throw new ArgumentNullException(nameof(readFile));
        credentials = new Lazy<BridgeFeishuCredentials>(
            Load,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public BridgeFeishuCredentials Credentials => credentials.Value;

    public string Name => "feishu-credentials";

    public BridgeComponentHealth ComponentHealth => Volatile.Read(ref started) == 1
        ? new(Name, "ready", "configured")
        : new(Name, "starting");

    internal string CredentialFilePath
    {
        get
        {
            var dataDirectory = Path.GetFullPath(options.DataDirectory);
            var configurationDirectory = Path.GetDirectoryName(dataDirectory) ??
                dataDirectory;
            return Path.Combine(configurationDirectory, ".env");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Credentials;
        Volatile.Write(ref started, 1);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        Volatile.Write(ref started, 0);
        return Task.CompletedTask;
    }

    private BridgeFeishuCredentials Load()
    {
        EnsureActive();
        var appId = readEnvironment(AppIdVariable);
        var appSecret = readEnvironment(AppSecretVariable);
        if (appId is null || appSecret is null)
        {
            var values = Parse(readFile(CredentialFilePath));
            appId ??= values.GetValueOrDefault(AppIdVariable);
            appSecret ??= values.GetValueOrDefault(AppSecretVariable);
        }

        appId = appId?.Trim();
        appSecret = appSecret?.Trim();
        var missing = new List<string>(2);
        if (string.IsNullOrEmpty(appId))
        {
            missing.Add(AppIdVariable);
        }
        if (string.IsNullOrEmpty(appSecret))
        {
            missing.Add(AppSecretVariable);
        }
        if (missing.Count != 0)
        {
            throw new InvalidOperationException(
                $"飞书生产凭据不完整，缺少 {string.Join(", ", missing)}。");
        }
        return new BridgeFeishuCredentials(appId!, appSecret!);
    }

    private void EnsureActive()
    {
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException(
                "飞书生产凭据源只能用于 Active Host。");
        }
    }

    private static IReadOnlyDictionary<string, string> Parse(string? content)
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
            if (key is not (AppIdVariable or AppSecretVariable))
            {
                continue;
            }
            values[key] = ParseValue(trimmed[(separator + 1)..]);
        }
        return values;
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
        if (closing == 0)
        {
            throw new InvalidDataException("飞书凭据 .env 包含未闭合的引号。");
        }
        var tail = value[(closing + 1)..].TrimStart();
        if (tail.Length != 0 && tail[0] != '#')
        {
            throw new InvalidDataException("飞书凭据 .env 的引号后包含非法内容。");
        }
        var parsed = value[1..closing];
        return quote == '"'
            ? parsed.Replace("\\n", "\n", StringComparison.Ordinal)
                .Replace("\\r", "\r", StringComparison.Ordinal)
            : parsed;
    }
}
