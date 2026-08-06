using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace AiCliFeishu.Bridge.Host;

public enum ActiveOwnerLeaseState
{
    Missing,
    Invalid,
    Live,
    Stale,
}

public sealed record ActiveOwnerLeaseRecord(
    int SchemaVersion,
    string HostKind,
    string OwnershipMode,
    int ProcessId,
    string InstanceName,
    string LeaseId,
    DateTimeOffset AcquiredAt);

public sealed record ActiveOwnerLeaseSnapshot(
    ActiveOwnerLeaseState State,
    ActiveOwnerLeaseRecord? Record = null);

public sealed class ActiveOwnerLeaseObserver
{
    public const string LockDirectoryName = "bridge-active-owner.lock";
    public const string MetadataFileName = "owner.json";
    public const int SchemaVersion = 1;

    private readonly Func<int, bool> processAlive;

    public ActiveOwnerLeaseObserver(BridgeHostOptions options) :
        this(options, IsProcessAlive)
    {
    }

    public ActiveOwnerLeaseObserver(
        BridgeHostOptions options,
        Func<int, bool> processAlive)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(processAlive);
        LockDirectoryPath = Path.Combine(
            options.DataDirectory,
            LockDirectoryName);
        MetadataPath = Path.Combine(LockDirectoryPath, MetadataFileName);
        this.processAlive = processAlive;
    }

    public string LockDirectoryPath { get; }

    public string MetadataPath { get; }

    public async ValueTask<ActiveOwnerLeaseSnapshot> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(LockDirectoryPath))
        {
            return new(ActiveOwnerLeaseState.Invalid);
        }
        if (!Directory.Exists(LockDirectoryPath))
        {
            return new(ActiveOwnerLeaseState.Missing);
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(MetadataPath, cancellationToken);
        }
        catch (DirectoryNotFoundException)
        {
            return new(ActiveOwnerLeaseState.Missing);
        }
        catch (FileNotFoundException)
        {
            return Directory.Exists(Path.GetDirectoryName(MetadataPath))
                ? new(ActiveOwnerLeaseState.Invalid)
                : new(ActiveOwnerLeaseState.Missing);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(ActiveOwnerLeaseState.Invalid);
        }

        ActiveOwnerLeaseRecord? record;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!HasExactProperties(document.RootElement))
            {
                return new(ActiveOwnerLeaseState.Invalid);
            }
            record = JsonSerializer.Deserialize<ActiveOwnerLeaseRecord>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = false,
                });
        }
        catch (JsonException)
        {
            return new(ActiveOwnerLeaseState.Invalid);
        }
        if (!IsValid(record))
        {
            return new(ActiveOwnerLeaseState.Invalid);
        }
        return processAlive(record!.ProcessId)
            ? new(ActiveOwnerLeaseState.Live, record)
            : new(ActiveOwnerLeaseState.Stale, record);
    }

    private static bool IsValid(ActiveOwnerLeaseRecord? record) =>
        record is not null &&
        record.SchemaVersion == SchemaVersion &&
        record.HostKind is "node" or "dotnet" &&
        record.OwnershipMode is "active" &&
        record.ProcessId > 0 &&
        IsValidInstanceName(record.InstanceName) &&
        IsValidLeaseId(record.LeaseId) &&
        record.AcquiredAt != default;

    private static bool HasExactProperties(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }
        var remaining = new HashSet<string>(StringComparer.Ordinal)
        {
            "schemaVersion",
            "hostKind",
            "ownershipMode",
            "processId",
            "instanceName",
            "leaseId",
            "acquiredAt",
        };
        foreach (var property in root.EnumerateObject())
        {
            if (!remaining.Remove(property.Name))
            {
                return false;
            }
        }
        return remaining.Count is 0;
    }

    private static bool IsValidInstanceName(string? value) => IsAsciiToken(value);

    private static bool IsValidLeaseId(string? value) => IsAsciiToken(value);

    private static bool IsAsciiToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return true;
        }
    }
}

public sealed class PassiveOwnerGuardSubsystem(
    ActiveOwnerLeaseObserver observer) :
    IBridgeHostSubsystem,
    IBridgeHostSubsystemHealth
{
    public string Name => "production-owner";

    public BridgeComponentHealth ComponentHealth { get; private set; } =
        new("production-owner", "starting");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var snapshot = await observer.InspectAsync(cancellationToken);
        ComponentHealth = new(
            Name,
            "passive",
            snapshot.State switch
            {
                ActiveOwnerLeaseState.Live =>
                    $"active-owner-{snapshot.Record!.HostKind}-live",
                ActiveOwnerLeaseState.Stale =>
                    $"active-owner-{snapshot.Record!.HostKind}-stale",
                ActiveOwnerLeaseState.Invalid => "active-owner-lease-invalid",
                _ => "active-owner-lease-missing",
            });
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        ComponentHealth = new(Name, "starting");
        return Task.CompletedTask;
    }
}
