using System.Globalization;
using System.Text.Json;

namespace AiCliFeishu.Bridge.Host;

internal sealed class BridgeActiveStartupAuthorization(
    BridgeHostOptions options,
    int processId)
{
    public void Confirm() => BridgeActiveStartupGate.Confirm(
        options,
        processId,
        maximumAttempts: 20,
        retryInterval: TimeSpan.FromMilliseconds(25));
}

internal static class BridgeActiveStartupGate
{
    internal const string CheckpointFileName =
        "bridge-host-cutover.checkpoint.json";

    private const int SchemaVersion = 1;
    private const int ManagementApiVersion = 1;
    private const int MaximumCheckpointBytes = 64 * 1024;

    public static BridgeActiveStartupAuthorization Authorize(
        BridgeHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Confirm(
            options,
            Environment.ProcessId,
            maximumAttempts: 51,
            retryInterval: TimeSpan.FromMilliseconds(100));
        return new(options, Environment.ProcessId);
    }

    internal static void Confirm(
        BridgeHostOptions options,
        int processId,
        int maximumAttempts = 1,
        TimeSpan? retryInterval = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options = options.Validate();
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException(
                "Active 启动授权只能用于 Active Bridge Host。");
        }
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }
        if (maximumAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        var interval = retryInterval ?? TimeSpan.Zero;
        if (interval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryInterval));
        }

        var checkpointPath = Path.Combine(
            options.DataDirectory,
            CheckpointFileName);
        GateInspection inspection = default;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            inspection = Inspect(checkpointPath, options, processId);
            if (inspection.Authorized)
            {
                return;
            }
            if (!inspection.Retryable)
            {
                throw Rejected(inspection.Reason);
            }
            if (attempt < maximumAttempts && interval > TimeSpan.Zero)
            {
                Thread.Sleep(interval);
            }
        }

        throw Rejected(inspection.Reason);
    }

    private static GateInspection Inspect(
        string checkpointPath,
        BridgeHostOptions options,
        int processId)
    {
        string json;
        try
        {
            using var stream = new FileStream(
                checkpointPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4_096,
                FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumCheckpointBytes)
            {
                return RejectedInspection("checkpoint-size-invalid");
            }
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            return Waiting("checkpoint-missing");
        }
        catch (DirectoryNotFoundException)
        {
            return Waiting("checkpoint-missing");
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException)
        {
            return Waiting("checkpoint-unavailable");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!HasExactProperties(
                    root,
                    "schemaVersion",
                    "operationId",
                    "updatedAt",
                    "stage",
                    "requiresRollback",
                    "failureReason",
                    "expectedNode",
                    "expectedDotNetInstanceName",
                    "dotNetProcessId",
                    "nodeRollbackProcessId"))
            {
                return RejectedInspection("checkpoint-shape-invalid");
            }
            if (!TryReadInt32(root, "schemaVersion", out var schemaVersion) ||
                schemaVersion != SchemaVersion ||
                !TryReadAsciiToken(root, "operationId", 128, out var operationId) ||
                !string.Equals(
                    operationId,
                    options.CutoverOperationId,
                    StringComparison.Ordinal) ||
                !TryReadTimestamp(root, "updatedAt") ||
                !TryReadText(root, "stage", out var stage) ||
                !TryReadBoolean(root, "requiresRollback", out var requiresRollback) ||
                requiresRollback ||
                !TryReadText(root, "failureReason", out var failureReason) ||
                failureReason is not "None" ||
                !TryReadAsciiToken(
                    root,
                    "expectedDotNetInstanceName",
                    128,
                    out var instanceName) ||
                !string.Equals(instanceName, options.InstanceName, StringComparison.Ordinal) ||
                !TryReadInt32(root, "dotNetProcessId", out var dotNetProcessId) ||
                !TryReadInt32(root, "nodeRollbackProcessId", out var rollbackProcessId) ||
                rollbackProcessId != 0 ||
                !root.TryGetProperty("expectedNode", out var expectedNode) ||
                !IsExpectedNode(expectedNode))
            {
                return RejectedInspection("checkpoint-evidence-mismatch");
            }

            return stage switch
            {
                "StoreHandoffVerified" when dotNetProcessId == 0 =>
                    Waiting("process-binding-pending"),
                "DotNetStartRequested" or "DotNetActiveVerified"
                    when dotNetProcessId == processId => Authorized(stage),
                "Completed" when dotNetProcessId > 0 => Authorized(stage),
                _ => RejectedInspection("checkpoint-stage-not-authorized"),
            };
        }
        catch (JsonException)
        {
            return RejectedInspection("checkpoint-json-invalid");
        }
    }

    private static bool IsExpectedNode(JsonElement node) =>
        HasExactProperties(
            node,
            "processId",
            "hostKind",
            "managementApiVersion",
            "ownershipMode",
            "activeOwner",
            "instanceName") &&
        TryReadInt32(node, "processId", out var processId) &&
        processId > 0 &&
        TryReadText(node, "hostKind", out var hostKind) &&
        hostKind is "node" &&
        TryReadInt32(node, "managementApiVersion", out var apiVersion) &&
        apiVersion == ManagementApiVersion &&
        TryReadText(node, "ownershipMode", out var ownershipMode) &&
        ownershipMode is "active" &&
        TryReadBoolean(node, "activeOwner", out var activeOwner) &&
        activeOwner &&
        TryReadAsciiToken(node, "instanceName", 128, out _);

    private static bool HasExactProperties(
        JsonElement element,
        params string[] expected)
    {
        if (element.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }
        var remaining = new HashSet<string>(expected, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!remaining.Remove(property.Name))
            {
                return false;
            }
        }
        return remaining.Count == 0;
    }

    private static bool TryReadInt32(
        JsonElement element,
        string propertyName,
        out int value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind is JsonValueKind.Number &&
            property.TryGetInt32(out value);
    }

    private static bool TryReadBoolean(
        JsonElement element,
        string propertyName,
        out bool value)
    {
        value = false;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return false;
        }
        value = property.GetBoolean();
        return true;
    }

    private static bool TryReadText(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is not JsonValueKind.String ||
            property.GetString() is not { } text)
        {
            return false;
        }
        value = text;
        return true;
    }

    private static bool TryReadAsciiToken(
        JsonElement element,
        string propertyName,
        int maximumLength,
        out string value) =>
        TryReadText(element, propertyName, out value) &&
        value.Length is > 0 &&
        value.Length <= maximumLength &&
        value.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                >= '0' and <= '9' or '-' or '_');

    private static bool TryReadTimestamp(
        JsonElement element,
        string propertyName) =>
        TryReadText(element, propertyName, out var value) &&
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var timestamp) &&
        timestamp != default;

    private static GateInspection Authorized(string stage) =>
        new(true, false, stage);

    private static GateInspection Waiting(string reason) =>
        new(false, true, reason);

    private static GateInspection RejectedInspection(string reason) =>
        new(false, false, reason);

    private static InvalidOperationException Rejected(string reason) => new(
        "C# Active Host 未取得有效的持久化切换启动授权。" +
        $"（{reason}）");

    private readonly record struct GateInspection(
        bool Authorized,
        bool Retryable,
        string Reason);
}
