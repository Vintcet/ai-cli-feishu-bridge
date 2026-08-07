using System.Globalization;
using System.Text;
using System.Text.Json;

var options = FixtureOptions.Parse(args);
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port}");
var app = builder.Build();

app.MapGet("/health", (HttpRequest request) =>
    HasToken(request, options.ControlToken)
        ? Results.Ok(Status(options))
        : Results.Ok(new { ok = true }));

app.MapGet("/control/status", (HttpRequest request) =>
    HasToken(request, options.ControlToken)
        ? Results.Ok(Status(options))
        : Results.Json(
            new { ok = false, error = "invalid token" },
            statusCode: StatusCodes.Status401Unauthorized));

app.MapPost("/control/shutdown", (
    HttpContext context,
    IHostApplicationLifetime lifetime) =>
{
    if (!context.Request.HasJsonContentType())
    {
        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
    }
    if (!HasToken(context.Request, options.ControlToken))
    {
        return Results.StatusCode(StatusCodes.Status401Unauthorized);
    }
    if (!HasExpectedIdentity(context.Request, options.HostKind))
    {
        return Results.StatusCode(StatusCodes.Status409Conflict);
    }

    context.Response.OnCompleted(() =>
    {
        lifetime.StopApplication();
        return Task.CompletedTask;
    });
    return Results.Accepted(value: new { ok = true });
});

FixtureLease? lease = null;
try
{
    if (options.DataDirectory is not null)
    {
        lease = FixtureLease.Acquire(options);
    }
    await app.RunAsync();
}
finally
{
    lease?.Dispose();
}

static object Status(FixtureOptions options) => new
{
    ok = true,
    hostKind = options.HostKind,
    managementApiVersion = 1,
    instanceName = options.InstanceName,
    ownershipMode = "active",
    activeOwner = true,
    processId = Environment.ProcessId,
};

static bool HasToken(HttpRequest request, string expected) =>
    string.Equals(
        request.Headers["X-AI-CLI-Feishu-Control-Token"].ToString(),
        expected,
        StringComparison.Ordinal);

static bool HasExpectedIdentity(HttpRequest request, string hostKind) =>
    string.Equals(
        request.Headers["X-AI-CLI-Feishu-Expected-Host-Kind"].ToString(),
        hostKind,
        StringComparison.Ordinal) &&
    string.Equals(
        request.Headers["X-AI-CLI-Feishu-Management-Api-Version"].ToString(),
        "1",
        StringComparison.Ordinal) &&
    int.TryParse(
        request.Headers["X-AI-CLI-Feishu-Expected-Process-Id"].ToString(),
        NumberStyles.None,
        CultureInfo.InvariantCulture,
        out var processId) &&
    processId == Environment.ProcessId;

internal sealed record FixtureOptions(
    string HostKind,
    string InstanceName,
    int Port,
    string ControlToken,
    string? DataDirectory)
{
    public static FixtureOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Fixture arguments must be --name value pairs.");
            }
            values[args[index]] = args[index + 1];
        }

        var hostKind = Required("--host-kind");
        if (hostKind is not "node" and not "dotnet")
        {
            throw new InvalidOperationException("--host-kind must be node or dotnet.");
        }
        var instanceName = Required("--instance");
        if (!int.TryParse(
                Required("--port"),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var port) || port is < 1 or > 65_535)
        {
            throw new InvalidOperationException("--port must be between 1 and 65535.");
        }
        return new(
            hostKind,
            instanceName,
            port,
            Required("--token"),
            OptionalPath("--data-directory"));

        string Required(string name) =>
            values.GetValueOrDefault(name) is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"Missing {name}.");

        string? OptionalPath(string name) =>
            values.GetValueOrDefault(name) is { Length: > 0 } value
                ? Path.GetFullPath(value)
                : null;
    }
}

internal sealed class FixtureLease : IDisposable
{
    private const string LockDirectoryName = "bridge-active-owner.lock";
    private const string MetadataFileName = "owner.json";

    private readonly string lockDirectory;
    private readonly string metadataPath;
    private readonly string leaseId;
    private bool disposed;

    private FixtureLease(
        string lockDirectory,
        string metadataPath,
        string leaseId)
    {
        this.lockDirectory = lockDirectory;
        this.metadataPath = metadataPath;
        this.leaseId = leaseId;
    }

    public static FixtureLease Acquire(FixtureOptions options)
    {
        var dataDirectory = options.DataDirectory ??
            throw new InvalidOperationException("Fixture data directory is missing.");
        Directory.CreateDirectory(dataDirectory);

        var lockDirectory = Path.Combine(dataDirectory, LockDirectoryName);
        var temporaryDirectory = Path.Combine(
            dataDirectory,
            $"{LockDirectoryName}.fixture-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        var leaseId = $"fixture-{Guid.NewGuid():N}";
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var temporaryMetadata = Path.Combine(temporaryDirectory, MetadataFileName);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                hostKind = options.HostKind,
                ownershipMode = "active",
                processId = Environment.ProcessId,
                instanceName = options.InstanceName,
                leaseId,
                acquiredAt = DateTimeOffset.UtcNow,
            }) + "\n");
            using (var stream = new FileStream(
                temporaryMetadata,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            Directory.Move(temporaryDirectory, lockDirectory);
            return new(
                lockDirectory,
                Path.Combine(lockDirectory, MetadataFileName),
                leaseId);
        }
        catch
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("processId", out var processId) ||
                processId.GetInt32() != Environment.ProcessId ||
                !root.TryGetProperty("leaseId", out var lease) ||
                !string.Equals(lease.GetString(), leaseId, StringComparison.Ordinal))
            {
                return;
            }

            File.Delete(metadataPath);
            Directory.Delete(lockDirectory, recursive: false);
        }
        catch (Exception error) when (
            error is DirectoryNotFoundException or FileNotFoundException)
        {
        }
    }
}
