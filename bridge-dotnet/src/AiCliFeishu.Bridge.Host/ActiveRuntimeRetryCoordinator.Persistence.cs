using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed partial class ActiveRuntimeRetryCoordinator
{
    private async Task ResetAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        lock (sync)
        {
            generations[sessionId] = GenerationLocked(sessionId) + 1;
            cycles.Remove(sessionId);
            attemptCounts.Remove(sessionId);
            retries = RetryStateMachine.CancelSession(retries, sessionId, clock.GetUtcNow());
        }
        await retryPersistenceGate.WaitAsync(cancellationToken);
        try
        {
            await ClearPersistedRetryStateCoreAsync(
                sessionId,
                expectedCycleId: null,
                cancellationToken);
        }
        finally
        {
            retryPersistenceGate.Release();
        }
    }

    private Task<bool> PersistRetryStateIfCurrentAsync(
        RetryCycle cycle,
        long expectedGeneration,
        string phase,
        CancellationToken cancellationToken) => PersistRetryStateAsync(
        cycle,
        phase,
        cancellationToken,
        expectedGeneration);

    private async Task<bool> PersistRetryStateAsync(
        RetryCycle cycle,
        string phase,
        CancellationToken cancellationToken,
        long? expectedGeneration = null)
    {
        await retryPersistenceGate.WaitAsync(cancellationToken);
        try
        {
            if (!RetryStateIsCurrent(cycle, expectedGeneration))
            {
                return false;
            }
            var persisted = new PersistedRetryState(
                cycle.CycleId,
                cycle.Runtime,
                cycle.TurnId,
                cycle.Error,
                cycle.Attempt,
                cycle.MaxAttempts,
                cycle.DueAt,
                cycle.TraceId,
                cycle.EventId,
                phase);
            await storeOwner.UpdateAsync(
                store => BridgeStoreBusinessStateMerger.PatchSessionExtensions(
                    store,
                    cycle.SessionId,
                    new Dictionary<string, JsonElement?>
                    {
                        [RetryStateExtension] =
                            JsonSerializer.SerializeToElement(persisted),
                    }),
                cancellationToken);
            if (RetryStateIsCurrent(cycle, expectedGeneration))
            {
                return true;
            }
            await ClearPersistedRetryStateCoreAsync(
                cycle.SessionId,
                cycle.CycleId,
                cancellationToken);
            return false;
        }
        finally
        {
            retryPersistenceGate.Release();
        }
    }

    private bool RetryStateIsCurrent(
        RetryCycle cycle,
        long? expectedGeneration)
    {
        lock (sync)
        {
            return cycles.GetValueOrDefault(cycle.SessionId) == cycle &&
                (expectedGeneration is null ||
                 GenerationLocked(cycle.SessionId) == expectedGeneration.Value);
        }
    }

    private async Task ClearPersistedRetryStateCoreAsync(
        string sessionId,
        string? expectedCycleId,
        CancellationToken cancellationToken)
    {
        await storeOwner.UpdateAsync(
            store =>
            {
                if (!store.Sessions.Sessions.TryGetValue(sessionId, out var session))
                {
                    return store;
                }
                if (expectedCycleId is not null &&
                    !string.Equals(
                        PersistedRetryStateOf(session)?.CycleId,
                        expectedCycleId,
                        StringComparison.Ordinal))
                {
                    return store;
                }
                return BridgeStoreBusinessStateMerger.PatchSessionExtensions(
                    store,
                    sessionId,
                    new Dictionary<string, JsonElement?>
                    {
                        [RetryStateExtension] = null,
                    });
            },
            cancellationToken);
    }

    private long Generation(string sessionId)
    {
        lock (sync)
        {
            return GenerationLocked(sessionId);
        }
    }

    private long GenerationLocked(string sessionId) =>
        generations.GetValueOrDefault(sessionId);

    private void EnsureStarted()
    {
        EnsureActive();
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!started)
            {
                throw new InvalidOperationException("Active Runtime 重试协调器尚未启动。");
            }
        }
    }

    private void EnsureActive()
    {
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException("Runtime 自动重试只能用于 Active Host。");
        }
    }

    private static string Runtime(SessionStoreRecord session) =>
        string.IsNullOrWhiteSpace(session.Runtime) ? RuntimeNames.Codex : session.Runtime;

    private static string RuntimeDisplayName(string runtime) => runtime switch
    {
        RuntimeNames.ClaudeCode => "Claude Code",
        RuntimeNames.OpenCode => "OpenCode",
        _ => "Codex",
    };

    private static string RequiredString(JsonElement value, string name) =>
        value.GetProperty(name).GetString()!;

    private static string? OptionalString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!.Trim()
            : null;

    private static string? ExtensionString(ExtensibleStoreObject value, string name) =>
        value.ExtensionData?.FirstOrDefault(item => string.Equals(
            item.Key,
            name,
            StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: JsonValueKind.String } item &&
        !string.IsNullOrWhiteSpace(item.Value.GetString())
            ? item.Value.GetString()!.Trim()
            : null;

    private static PersistedRetryState? PersistedRetryStateOf(
        ExtensibleStoreObject value)
    {
        if (value.ExtensionData?.FirstOrDefault(item => string.Equals(
                item.Key,
                RetryStateExtension,
                StringComparison.OrdinalIgnoreCase)) is not
            { Value.ValueKind: JsonValueKind.Object } item)
        {
            return null;
        }
        try
        {
            return item.Value.Deserialize<PersistedRetryState>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ExtensionBoolean(ExtensibleStoreObject value, string name) =>
        value.ExtensionData?.FirstOrDefault(item => string.Equals(
            item.Key,
            name,
            StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: JsonValueKind.True };

    private static Dictionary<string, JsonElement>? CloneExtensions(
        Dictionary<string, JsonElement>? extensions) => extensions?.ToDictionary(
            item => item.Key,
            item => item.Value.Clone(),
            StringComparer.Ordinal);

    private static string ShortId(string sessionId)
    {
        var compact = new string(sessionId.Where(char.IsLetterOrDigit).ToArray());
        var source = compact.Length == 0 ? sessionId : compact;
        return source[^Math.Min(8, source.Length)..].ToLowerInvariant();
    }
}
