using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiCliFeishuControl;

internal sealed record BridgeHostCutoverCheckpoint(
    int SchemaVersion,
    string OperationId,
    DateTimeOffset UpdatedAt,
    BridgeHostCutoverStage Stage,
    bool RequiresRollback,
    BridgeCutoverFailureReason FailureReason,
    BridgeCutoverHostIdentity ExpectedNode,
    string ExpectedDotNetInstanceName,
    int DotNetProcessId,
    int NodeRollbackProcessId)
{
    public const int CurrentSchemaVersion = 1;

    public BridgeHostCutoverSnapshot ToSnapshot() =>
        new(Stage, RequiresRollback, FailureReason);

    public BridgeHostCutoverCheckpoint Validate()
    {
        BridgeHostCutoverCheckpointValidator.Validate(this);
        return this;
    }
}

internal static class BridgeHostCutoverCheckpointValidator
{
    private const int MaximumOperationIdLength = 128;

    internal static bool IsValidOperationId(string? operationId) =>
        IsAsciiToken(operationId) && operationId!.Length <= MaximumOperationIdLength;

    public static void Validate(BridgeHostCutoverCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.SchemaVersion != BridgeHostCutoverCheckpoint.CurrentSchemaVersion)
        {
            throw Invalid("检查点版本不受支持。");
        }
        if (!IsValidOperationId(checkpoint.OperationId))
        {
            throw Invalid("检查点 operationId 无效。");
        }
        if (checkpoint.UpdatedAt == default)
        {
            throw Invalid("检查点更新时间不能为空。");
        }
        if (!Enum.IsDefined(checkpoint.Stage) ||
            !Enum.IsDefined(checkpoint.FailureReason) ||
            checkpoint.FailureReason is BridgeCutoverFailureReason.InvalidEventOrder)
        {
            throw Invalid("检查点阶段或失败原因无效。");
        }
        ArgumentNullException.ThrowIfNull(checkpoint.ExpectedNode);
        if (!checkpoint.ExpectedNode.IsNodeActive(
                BridgeHostCutoverTransaction.CurrentManagementApiVersion) ||
            !IsAsciiToken(checkpoint.ExpectedNode.InstanceName))
        {
            throw Invalid("检查点预期 Node 身份无效。");
        }
        if (!IsAsciiToken(checkpoint.ExpectedDotNetInstanceName))
        {
            throw Invalid("检查点预期 .NET 实例名无效。");
        }
        if (checkpoint.DotNetProcessId < 0 || checkpoint.NodeRollbackProcessId < 0)
        {
            throw Invalid("检查点进程号不能为负数。");
        }
        if (!IsValidSnapshotShape(checkpoint))
        {
            throw Invalid("检查点阶段、回退标记、失败原因和进程号组合无效。");
        }
    }

    public static bool IsValid(BridgeHostCutoverCheckpoint? checkpoint)
    {
        try
        {
            Validate(checkpoint!);
            return true;
        }
        catch (Exception error) when (
            error is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            return false;
        }
    }

    public static bool HasExactProperties(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }
        return HasExactProperties(
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
            "nodeRollbackProcessId");
    }

    public static bool HasExactExpectedNodeProperties(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }
        return HasExactProperties(
            root,
            "processId",
            "hostKind",
            "managementApiVersion",
            "ownershipMode",
            "activeOwner",
            "instanceName");
    }

    public static bool HasExactEnumValue<TEnum>(
        JsonElement root,
        string property)
        where TEnum : struct, Enum
    {
        if (!root.TryGetProperty(property, out var value) ||
            value.ValueKind is not JsonValueKind.String)
        {
            return false;
        }
        var text = value.GetString();
        return text is not null &&
            Enum.GetNames<TEnum>().Contains(text, StringComparer.Ordinal);
    }

    private static bool IsValidSnapshotShape(BridgeHostCutoverCheckpoint checkpoint)
    {
        var hasFailure = checkpoint.FailureReason is not BridgeCutoverFailureReason.None;
        var stage = checkpoint.Stage;
        if (stage is BridgeHostCutoverStage.Planned or
            BridgeHostCutoverStage.NodeStopRequested or
            BridgeHostCutoverStage.NodeOfflineVerified or
            BridgeHostCutoverStage.StoreHandoffVerified)
        {
            return !checkpoint.RequiresRollback &&
                !hasFailure &&
                checkpoint.DotNetProcessId is 0 &&
                checkpoint.NodeRollbackProcessId is 0;
        }
        if (stage is BridgeHostCutoverStage.DotNetStartRequested or
            BridgeHostCutoverStage.DotNetActiveVerified)
        {
            return !checkpoint.RequiresRollback &&
                !hasFailure &&
                checkpoint.DotNetProcessId > 0 &&
                checkpoint.NodeRollbackProcessId is 0;
        }
        if (stage is BridgeHostCutoverStage.Completed)
        {
            return !checkpoint.RequiresRollback &&
                !hasFailure &&
                checkpoint.DotNetProcessId > 0 &&
                checkpoint.NodeRollbackProcessId is 0;
        }
        if (stage is BridgeHostCutoverStage.RollbackRequired)
        {
            return checkpoint.RequiresRollback && hasFailure &&
                checkpoint.NodeRollbackProcessId is 0;
        }
        if (stage is BridgeHostCutoverStage.DotNetStopRequested or
            BridgeHostCutoverStage.DotNetOfflineVerified)
        {
            return checkpoint.RequiresRollback && hasFailure &&
                checkpoint.DotNetProcessId > 0 &&
                checkpoint.NodeRollbackProcessId is 0;
        }
        if (stage is BridgeHostCutoverStage.NodeRollbackStartRequested)
        {
            return checkpoint.RequiresRollback && hasFailure &&
                checkpoint.NodeRollbackProcessId > 0;
        }
        if (stage is BridgeHostCutoverStage.RolledBack)
        {
            return !checkpoint.RequiresRollback && hasFailure &&
                checkpoint.NodeRollbackProcessId > 0;
        }
        if (stage is BridgeHostCutoverStage.FailedSafe)
        {
            return hasFailure &&
                (checkpoint.RequiresRollback ||
                    checkpoint.DotNetProcessId is 0 &&
                    checkpoint.NodeRollbackProcessId is 0);
        }
        return false;
    }

    private static bool HasExactProperties(
        JsonElement root,
        params string[] expected)
    {
        var remaining = new HashSet<string>(expected, StringComparer.Ordinal);
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

    private static InvalidDataException Invalid(string message) =>
        new(message);
}

internal enum BridgeHostCutoverCheckpointReadState
{
    Missing,
    Present,
    Invalid,
    Unavailable,
}

internal sealed record BridgeHostCutoverCheckpointReadResult(
    BridgeHostCutoverCheckpointReadState State,
    BridgeHostCutoverCheckpoint? Checkpoint = null,
    string? FileVersion = null);

internal static class BridgeHostCutoverCheckpointJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(BridgeHostCutoverCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        checkpoint.Validate();
        return JsonSerializer.Serialize(checkpoint, Options) + "\n";
    }

    public static BridgeHostCutoverCheckpoint Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!BridgeHostCutoverCheckpointValidator.HasExactProperties(document.RootElement) ||
            !BridgeHostCutoverCheckpointValidator.HasExactEnumValue<BridgeHostCutoverStage>(
                document.RootElement,
                "stage") ||
            !BridgeHostCutoverCheckpointValidator.HasExactEnumValue<BridgeCutoverFailureReason>(
                document.RootElement,
                "failureReason") ||
            !document.RootElement.TryGetProperty("expectedNode", out var expectedNode) ||
            !BridgeHostCutoverCheckpointValidator.HasExactExpectedNodeProperties(expectedNode))
        {
            throw new InvalidDataException("切换检查点 JSON 字段不完整或包含未知字段。");
        }
        var checkpoint = JsonSerializer.Deserialize<BridgeHostCutoverCheckpoint>(json, Options) ??
            throw new InvalidDataException("切换检查点 JSON 为空。");
        checkpoint.Validate();
        return checkpoint;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(
            namingPolicy: null,
            allowIntegerValues: false));
        return options;
    }
}
