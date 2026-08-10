using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace AiCliFeishu.Bridge.Adapters.Storage;

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
    private static readonly TimeSpan processStartTolerance = TimeSpan.FromSeconds(5);
    public const string LockDirectoryName = "bridge-active-owner.lock";
    public const string MetadataFileName = "owner.json";
    public const string OwnershipHandleFileName = "owner.lck";
    public const int SchemaVersion = 1;

    private readonly Func<int, bool> processAlive;
    private readonly Func<int, DateTimeOffset?> processStartedAt;

    public ActiveOwnerLeaseObserver(string dataDirectory) :
        this(dataDirectory, IsProcessAlive, TryGetProcessStartedAt)
    {
    }

    public ActiveOwnerLeaseObserver(
        string dataDirectory,
        Func<int, bool> processAlive,
        Func<int, DateTimeOffset?>? processStartedAt = null)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException(
                "Active Owner 数据目录不能为空。",
                nameof(dataDirectory));
        }
        ArgumentNullException.ThrowIfNull(processAlive);
        var fullDataDirectory = Path.GetFullPath(dataDirectory);
        LockDirectoryPath = Path.Combine(
            fullDataDirectory,
            LockDirectoryName);
        MetadataPath = Path.Combine(LockDirectoryPath, MetadataFileName);
        OwnershipHandlePath = Path.Combine(LockDirectoryPath, OwnershipHandleFileName);
        this.processAlive = processAlive;
        this.processStartedAt = processStartedAt ?? (_ => null);
    }

    public string LockDirectoryPath { get; }

    public string MetadataPath { get; }

    public string OwnershipHandlePath { get; }

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
        if (!IsValidRecord(record))
        {
            return new(ActiveOwnerLeaseState.Invalid);
        }
        var handleHeld = IsOwnershipHandleHeld();
        if (handleHeld is not null)
        {
            return handleHeld.Value
                ? new(ActiveOwnerLeaseState.Live, record)
                : new(ActiveOwnerLeaseState.Stale, record);
        }
        if (!processAlive(record!.ProcessId))
        {
            return new(ActiveOwnerLeaseState.Stale, record);
        }
        var startedAt = processStartedAt(record.ProcessId);
        return startedAt is not null &&
               startedAt.Value > record.AcquiredAt + processStartTolerance
            ? new(ActiveOwnerLeaseState.Stale, record)
            : new(ActiveOwnerLeaseState.Live, record);
    }

    public static bool IsValidRecord(ActiveOwnerLeaseRecord? record) =>
        record is not null &&
        record.SchemaVersion == SchemaVersion &&
        record.HostKind is "dotnet" &&
        record.OwnershipMode is "active" &&
        record.ProcessId > 0 &&
        IsAsciiToken(record.InstanceName) &&
        IsAsciiToken(record.LeaseId) &&
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

    private bool? IsOwnershipHandleHeld()
    {
        if (!File.Exists(OwnershipHandlePath))
        {
            return null;
        }
        try
        {
            using var probe = new FileStream(
                OwnershipHandlePath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static DateTimeOffset? TryGetProcessStartedAt(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited
                ? null
                : new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception error) when (
            error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }
}
