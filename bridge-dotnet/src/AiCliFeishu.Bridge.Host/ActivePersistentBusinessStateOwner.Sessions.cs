using System.Globalization;
using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed partial class ActivePersistentBusinessStateOwner
{
    public async ValueTask<BridgeSessionAliasUpdateResult> UpdateSessionAliasAsync(
        string sessionId,
        string? alias,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        string? normalizedAlias = null;
        if (alias is not null)
        {
            var validationError = SessionAliasRules.ValidationError(alias);
            if (validationError is not null)
            {
                return new(null, null, validationError);
            }
            normalizedAlias = SessionAliasRules.Normalize(alias);
        }

        await writeGate.WaitAsync(cancellationToken);
        try
        {
            _ = RequireInitialized();
            var observed = await storeOwner.ReadAsync(cancellationToken);
            var rejection = AliasUpdateRejection(
                observed,
                sessionId,
                normalizedAlias);
            if (rejection is not null)
            {
                return rejection;
            }

            BridgeSessionAliasUpdateResult? result = null;
            await storeOwner.UpdateAsync(
                store =>
                {
                    var currentRejection = AliasUpdateRejection(
                        store,
                        sessionId,
                        normalizedAlias);
                    if (currentRejection is not null)
                    {
                        result = currentRejection;
                        return store;
                    }

                    var updated = BridgeStoreBusinessStateMerger.PatchSessionExtensions(
                        store,
                        sessionId,
                        new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                        {
                            ["alias"] = normalizedAlias is null
                                ? null
                                : JsonSerializer.SerializeToElement(normalizedAlias),
                        });
                    result = new(
                        updated.Sessions.Sessions[sessionId],
                        null,
                        null);
                    return updated;
                },
                cancellationToken);
            return result ?? throw new InvalidOperationException(
                "会话别名更新没有产生结果。 ");
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask<BridgeSessionHistoryHideResult>
        HideSessionFromHistoryAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            _ = RequireInitialized();
            var observed = await storeOwner.ReadAsync(cancellationToken);
            var rejection = HistoryHideRejection(observed, sessionId);
            if (rejection is not null)
            {
                return rejection;
            }

            BridgeSessionHistoryHideResult? result = null;
            await storeOwner.UpdateAsync(
                store =>
                {
                    var currentRejection = HistoryHideRejection(store, sessionId);
                    if (currentRejection is not null)
                    {
                        result = currentRejection;
                        return store;
                    }

                    var session = store.Sessions.Sessions[sessionId];
                    if (HasNonEmptyExtension(session, "historyHiddenAt"))
                    {
                        result = new(session, null);
                        return store;
                    }
                    var updated = BridgeStoreBusinessStateMerger.PatchSessionExtensions(
                        store,
                        sessionId,
                        new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                        {
                            ["historyHiddenAt"] = JsonSerializer.SerializeToElement(
                                clock.GetUtcNow().ToUniversalTime().ToString("O")),
                        });
                    result = new(updated.Sessions.Sessions[sessionId], null);
                    return updated;
                },
                cancellationToken);
            return result ?? throw new InvalidOperationException(
                "历史会话隐藏没有产生结果。 ");
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask<BridgeSessionGroupNameUpdateResult>
        EnsureSessionGroupOrdinalAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            _ = RequireInitialized();
            var observed = await storeOwner.ReadAsync(cancellationToken);
            var prepared = AssignSessionGroupOrdinals(observed, sessionId);
            if (prepared.Error is not null || !prepared.Changed)
            {
                return new(prepared.Session, prepared.Error);
            }

            BridgeSessionGroupNameUpdateResult? result = null;
            await storeOwner.UpdateAsync(
                store =>
                {
                    var current = AssignSessionGroupOrdinals(store, sessionId);
                    result = new(current.Session, current.Error);
                    return current.Store;
                },
                cancellationToken);
            return result ?? throw new InvalidOperationException(
                "会话群序号更新没有产生结果。 ");
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask<BridgeSessionGroupNameUpdateResult>
        BindSessionGroupAsync(
            string sessionId,
            int expectedOrdinal,
            string expectedOwnerOpenId,
            string chatId,
            string name,
            DateTimeOffset createdAt,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedOrdinal);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOwnerOpenId);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > SessionGroupNameRules.MaximumLength)
        {
            return new(
                null,
                $"飞书群名称最多 {SessionGroupNameRules.MaximumLength} 个字符。");
        }

        var createdAtText = createdAt.ToUniversalTime().ToString("O");
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            _ = RequireInitialized();
            var observed = await storeOwner.ReadAsync(cancellationToken);
            var rejection = SessionGroupBindingRejection(
                observed,
                sessionId,
                expectedOrdinal,
                expectedOwnerOpenId,
                chatId,
                requireUnbound: false);
            if (rejection is not null)
            {
                return rejection;
            }
            var observedSession = observed.Sessions.Sessions[sessionId];
            if (SessionGroupBindingMatches(
                    observedSession,
                    chatId,
                    name,
                    createdAtText))
            {
                return new(observedSession, null);
            }

            BridgeSessionGroupNameUpdateResult? result = null;
            await storeOwner.UpdateAsync(
                store =>
                {
                    var currentRejection = SessionGroupBindingRejection(
                        store,
                        sessionId,
                        expectedOrdinal,
                        expectedOwnerOpenId,
                        chatId,
                        requireUnbound: false);
                    if (currentRejection is not null)
                    {
                        result = currentRejection;
                        return store;
                    }

                    var current = store.Sessions.Sessions[sessionId];
                    if (SessionGroupBindingMatches(
                            current,
                            chatId,
                            name,
                            createdAtText))
                    {
                        result = new(current, null);
                        return store;
                    }
                    var updated = BridgeStoreBusinessStateMerger.PatchSessionExtensions(
                        store,
                        sessionId,
                        new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                        {
                            ["feishuChatId"] = JsonSerializer.SerializeToElement(chatId),
                            ["feishuChatName"] = JsonSerializer.SerializeToElement(name),
                            ["feishuChatCreatedAt"] =
                                JsonSerializer.SerializeToElement(createdAtText),
                            ["feishuChatError"] = null,
                            ["feishuChatErrorAt"] = null,
                        });
                    result = new(updated.Sessions.Sessions[sessionId], null);
                    return updated;
                },
                cancellationToken);
            return result ?? throw new InvalidOperationException(
                "会话群绑定更新没有产生结果。 ");
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask<BridgeSessionGroupNameUpdateResult>
        RecordSessionGroupErrorAsync(
            string sessionId,
            int expectedOrdinal,
            string expectedOwnerOpenId,
            string error,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedOrdinal);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOwnerOpenId);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        if (error.Length > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(error),
                "会话群错误详情最多保留 500 个字符。");
        }

        var observedAtText = observedAt.ToUniversalTime().ToString("O");
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            _ = RequireInitialized();
            var observed = await storeOwner.ReadAsync(cancellationToken);
            var rejection = SessionGroupBindingRejection(
                observed,
                sessionId,
                expectedOrdinal,
                expectedOwnerOpenId,
                expectedChatId: null,
                requireUnbound: true);
            if (rejection is not null)
            {
                return rejection;
            }

            BridgeSessionGroupNameUpdateResult? result = null;
            await storeOwner.UpdateAsync(
                store =>
                {
                    var currentRejection = SessionGroupBindingRejection(
                        store,
                        sessionId,
                        expectedOrdinal,
                        expectedOwnerOpenId,
                        expectedChatId: null,
                        requireUnbound: true);
                    if (currentRejection is not null)
                    {
                        result = currentRejection;
                        return store;
                    }
                    var updated = BridgeStoreBusinessStateMerger.PatchSessionExtensions(
                        store,
                        sessionId,
                        new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                        {
                            ["feishuChatError"] = JsonSerializer.SerializeToElement(error),
                            ["feishuChatErrorAt"] =
                                JsonSerializer.SerializeToElement(observedAtText),
                        });
                    result = new(updated.Sessions.Sessions[sessionId], null);
                    return updated;
                },
                cancellationToken);
            return result ?? throw new InvalidOperationException(
                "会话群错误更新没有产生结果。 ");
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask<BridgeSessionGroupNameUpdateResult>
        ClearSessionGroupErrorAsync(
            string sessionId,
            int expectedOrdinal,
            string expectedOwnerOpenId,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedOrdinal);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOwnerOpenId);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            _ = RequireInitialized();
            var observed = await storeOwner.ReadAsync(cancellationToken);
            var rejection = SessionGroupBindingRejection(
                observed,
                sessionId,
                expectedOrdinal,
                expectedOwnerOpenId,
                expectedChatId: null,
                requireUnbound: true);
            if (rejection is not null)
            {
                return rejection;
            }
            var observedSession = observed.Sessions.Sessions[sessionId];
            if (ExtensionString(observedSession, "feishuChatError") is null &&
                ExtensionString(observedSession, "feishuChatErrorAt") is null)
            {
                return new(observedSession, null);
            }

            BridgeSessionGroupNameUpdateResult? result = null;
            await storeOwner.UpdateAsync(
                store =>
                {
                    var currentRejection = SessionGroupBindingRejection(
                        store,
                        sessionId,
                        expectedOrdinal,
                        expectedOwnerOpenId,
                        expectedChatId: null,
                        requireUnbound: true);
                    if (currentRejection is not null)
                    {
                        result = currentRejection;
                        return store;
                    }
                    var current = store.Sessions.Sessions[sessionId];
                    if (ExtensionString(current, "feishuChatError") is null &&
                        ExtensionString(current, "feishuChatErrorAt") is null)
                    {
                        result = new(current, null);
                        return store;
                    }
                    var updated = BridgeStoreBusinessStateMerger.PatchSessionExtensions(
                        store,
                        sessionId,
                        new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                        {
                            ["feishuChatError"] = null,
                            ["feishuChatErrorAt"] = null,
                        });
                    result = new(updated.Sessions.Sessions[sessionId], null);
                    return updated;
                },
                cancellationToken);
            return result ?? throw new InvalidOperationException(
                "会话群错误清除更新没有产生结果。 ");
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask<BridgeSessionGroupNameUpdateResult>
        ClearSessionGroupAsync(
            string sessionId,
            string expectedChatId,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedChatId);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            _ = RequireInitialized();
            var observed = await storeOwner.ReadAsync(cancellationToken);
            var rejection = SessionGroupNameUpdateRejection(
                observed,
                sessionId,
                expectedChatId);
            if (rejection is not null)
            {
                return rejection;
            }

            BridgeSessionGroupNameUpdateResult? result = null;
            await storeOwner.UpdateAsync(
                store =>
                {
                    var currentRejection = SessionGroupNameUpdateRejection(
                        store,
                        sessionId,
                        expectedChatId);
                    if (currentRejection is not null)
                    {
                        result = currentRejection;
                        return store;
                    }

                    var updated = BridgeStoreBusinessStateMerger.PatchSessionExtensions(
                        store,
                        sessionId,
                        new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                        {
                            ["feishuChatId"] = null,
                            ["feishuChatName"] = null,
                            ["feishuChatCreatedAt"] = null,
                            ["feishuChatError"] = null,
                            ["feishuChatErrorAt"] = null,
                        });
                    result = new(updated.Sessions.Sessions[sessionId], null);
                    return updated;
                },
                cancellationToken);
            return result ?? throw new InvalidOperationException(
                "会话群解绑更新没有产生结果。 ");
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask<BridgeSessionGroupNameUpdateResult>
        UpdateSessionGroupNameAsync(
            string sessionId,
            string expectedChatId,
            string name,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedChatId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > SessionGroupNameRules.MaximumLength)
        {
            return new(
                null,
                $"飞书群名称最多 {SessionGroupNameRules.MaximumLength} 个字符。");
        }

        await writeGate.WaitAsync(cancellationToken);
        try
        {
            _ = RequireInitialized();
            var observed = await storeOwner.ReadAsync(cancellationToken);
            var rejection = SessionGroupNameUpdateRejection(
                observed,
                sessionId,
                expectedChatId);
            if (rejection is not null)
            {
                return rejection;
            }
            var observedSession = observed.Sessions.Sessions[sessionId];
            if (string.Equals(
                    ExtensionString(observedSession, "feishuChatName"),
                    name,
                    StringComparison.Ordinal))
            {
                return new(observedSession, null);
            }

            BridgeSessionGroupNameUpdateResult? result = null;
            await storeOwner.UpdateAsync(
                store =>
                {
                    var currentRejection = SessionGroupNameUpdateRejection(
                        store,
                        sessionId,
                        expectedChatId);
                    if (currentRejection is not null)
                    {
                        result = currentRejection;
                        return store;
                    }

                    var current = store.Sessions.Sessions[sessionId];
                    if (string.Equals(
                            ExtensionString(current, "feishuChatName"),
                            name,
                            StringComparison.Ordinal))
                    {
                        result = new(current, null);
                        return store;
                    }

                    var updated = BridgeStoreBusinessStateMerger.PatchSessionExtensions(
                        store,
                        sessionId,
                        new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                        {
                            ["feishuChatName"] =
                                JsonSerializer.SerializeToElement(name),
                        });
                    result = new(updated.Sessions.Sessions[sessionId], null);
                    return updated;
                },
                cancellationToken);
            return result ?? throw new InvalidOperationException(
                "会话群名称更新没有产生结果。 ");
        }
        finally
        {
            writeGate.Release();
        }
    }


    private static bool IsAliasReserved(SessionStoreRecord session)
    {
        if (!string.Equals(
                session.Status,
                SessionStatuses.Ended,
                StringComparison.Ordinal))
        {
            return true;
        }
        if (session.SessionId.StartsWith(
                "managed-terminal-",
                StringComparison.Ordinal))
        {
            return false;
        }
        return ExtensionBoolean(session, "historyEligible") &&
            !HasNonEmptyExtension(session, "historyHiddenAt");
    }

    private static BridgeSessionAliasUpdateResult? AliasUpdateRejection(
        BridgeStoreSnapshot store,
        string sessionId,
        string? normalizedAlias)
    {
        if (!store.Sessions.Sessions.TryGetValue(sessionId, out var session) ||
            !IsAliasReserved(session))
        {
            return new(null, null, "会话不存在或已经失效。");
        }
        if (normalizedAlias is null)
        {
            return null;
        }

        var aliasKey = SessionAliasRules.Key(normalizedAlias);
        var conflict = store.Sessions.Sessions.Values
            .Where(IsAliasReserved)
            .FirstOrDefault(candidate =>
                !string.Equals(
                    candidate.SessionId,
                    sessionId,
                    StringComparison.Ordinal) &&
                ExtensionString(candidate, "alias") is { } currentAlias &&
                SessionAliasRules.Key(currentAlias) == aliasKey);
        return conflict is null ? null : new(null, conflict, null);
    }

    private static BridgeSessionHistoryHideResult? HistoryHideRejection(
        BridgeStoreSnapshot store,
        string sessionId)
    {
        if (!store.Sessions.Sessions.TryGetValue(sessionId, out var session) ||
            !ExtensionBoolean(session, "historyEligible") ||
            session.SessionId.StartsWith(
                "managed-terminal-",
                StringComparison.Ordinal))
        {
            return new(
                null,
                "历史记录不存在，或不是由助手创建的会话。");
        }
        return null;
    }

    private static BridgeSessionGroupNameUpdateResult?
        SessionGroupNameUpdateRejection(
            BridgeStoreSnapshot store,
            string sessionId,
            string expectedChatId)
    {
        if (!store.Sessions.Sessions.TryGetValue(sessionId, out var session))
        {
            return new(null, "会话不存在或已经失效。");
        }
        if (!string.Equals(
                ExtensionString(session, "feishuChatId"),
                expectedChatId,
                StringComparison.Ordinal))
        {
            return new(null, "会话群绑定已变化，请重试。");
        }
        return null;
    }

    private static SessionGroupOrdinalMutation AssignSessionGroupOrdinals(
        BridgeStoreSnapshot store,
        string sessionId)
    {
        if (!store.Sessions.Sessions.TryGetValue(sessionId, out var target))
        {
            return new(store, null, false, "会话不存在或已经失效。");
        }

        var scope = SessionGroupScopeKey(target);
        var siblings = store.Sessions.Sessions.Values
            .Where(session =>
                string.Equals(
                    SessionGroupScopeKey(session),
                    scope,
                    StringComparison.Ordinal) &&
                (string.Equals(
                     session.SessionId,
                     sessionId,
                     StringComparison.Ordinal) ||
                 ExtensionBoolean(session, "managedByAssistant") ||
                 ExtensionString(session, "feishuChatId") is not null ||
                 ExtensionPositiveInteger(session, "feishuChatOrdinal") is not null))
            .OrderBy(SessionGroupOrderTime)
            .ThenBy(session => session.SessionId, StringComparer.Ordinal)
            .ToArray();
        var used = siblings
            .Select(session => ExtensionPositiveInteger(
                session,
                "feishuChatOrdinal"))
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToHashSet();

        var updated = store;
        var changed = false;
        foreach (var session in siblings)
        {
            if (ExtensionPositiveInteger(
                    session,
                    "feishuChatOrdinal") is not null)
            {
                continue;
            }
            var ordinal = 1;
            while (used.Contains(ordinal))
            {
                ordinal++;
            }
            updated = BridgeStoreBusinessStateMerger.PatchSessionExtensions(
                updated,
                session.SessionId,
                new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                {
                    ["feishuChatOrdinal"] = JsonSerializer.SerializeToElement(ordinal),
                });
            used.Add(ordinal);
            changed = true;
        }
        return new(
            updated,
            updated.Sessions.Sessions[sessionId],
            changed,
            null);
    }

    private static BridgeSessionGroupNameUpdateResult?
        SessionGroupBindingRejection(
            BridgeStoreSnapshot store,
            string sessionId,
            int expectedOrdinal,
            string expectedOwnerOpenId,
            string? expectedChatId,
            bool requireUnbound)
    {
        if (!store.Sessions.Sessions.TryGetValue(sessionId, out var session) ||
            !ExtensionBoolean(session, "managedByAssistant"))
        {
            return new(null, "会话不存在，或不是由助手创建的。");
        }
        if (!string.Equals(
                store.Bindings.OwnerOpenId,
                expectedOwnerOpenId,
                StringComparison.Ordinal))
        {
            return new(null, "飞书管理员绑定已变化，请重试。");
        }
        if (ExtensionPositiveInteger(session, "feishuChatOrdinal") !=
            expectedOrdinal)
        {
            return new(null, "会话群序号已变化，请重试。");
        }
        var currentChatId = ExtensionString(session, "feishuChatId");
        if (requireUnbound && currentChatId is not null ||
            expectedChatId is not null &&
            currentChatId is not null &&
            !string.Equals(
                currentChatId,
                expectedChatId,
                StringComparison.Ordinal))
        {
            return new(null, "会话群绑定已变化，请重试。");
        }
        return null;
    }

    private static bool SessionGroupBindingMatches(
        SessionStoreRecord session,
        string chatId,
        string name,
        string createdAt) =>
        string.Equals(
            ExtensionString(session, "feishuChatId"),
            chatId,
            StringComparison.Ordinal) &&
        string.Equals(
            ExtensionString(session, "feishuChatName"),
            name,
            StringComparison.Ordinal) &&
        string.Equals(
            ExtensionString(session, "feishuChatCreatedAt"),
            createdAt,
            StringComparison.Ordinal) &&
        ExtensionString(session, "feishuChatError") is null &&
        ExtensionString(session, "feishuChatErrorAt") is null;

    private static string SessionGroupScopeKey(SessionStoreRecord session)
    {
        var runtime = string.IsNullOrWhiteSpace(session.Runtime)
            ? RuntimeNames.Codex
            : session.Runtime;
        var project = (session.ProjectName ?? string.Empty)
            .Trim()
            .Normalize(NormalizationForm.FormC)
            .ToLower(CultureInfo.GetCultureInfo("en-US"));
        return $"{runtime}\0{project}";
    }

    private static DateTimeOffset SessionGroupOrderTime(
        SessionStoreRecord session)
    {
        var value = ExtensionString(session, "feishuChatCreatedAt") ??
            session.OpenedAt;
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }

    private static int? ExtensionPositiveInteger(
        ExtensibleStoreObject value,
        string name) =>
        value.ExtensionData is not null &&
        value.ExtensionData.FirstOrDefault(item => string.Equals(
            item.Key,
            name,
            StringComparison.OrdinalIgnoreCase))
            is { Value.ValueKind: JsonValueKind.Number } item &&
        item.Value.TryGetInt32(out var number) &&
        number > 0
            ? number
            : null;

    private sealed record SessionGroupOrdinalMutation(
        BridgeStoreSnapshot Store,
        SessionStoreRecord? Session,
        bool Changed,
        string? Error);

    private static bool HasNonEmptyExtension(
        ExtensibleStoreObject value,
        string name) =>
        value.ExtensionData is not null &&
        value.ExtensionData.Any(item =>
            string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase) &&
            item.Value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(item.Value.GetString()));

    private static bool ExtensionBoolean(
        ExtensibleStoreObject value,
        string name) =>
        value.ExtensionData is not null &&
        value.ExtensionData.Any(item =>
            string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase) &&
            item.Value.ValueKind is JsonValueKind.True);

    private static string? ExtensionString(
        ExtensibleStoreObject value,
        string name) =>
        value.ExtensionData is not null &&
        value.ExtensionData.FirstOrDefault(item =>
            string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase))
            is { Value.ValueKind: JsonValueKind.String } item &&
        !string.IsNullOrWhiteSpace(item.Value.GetString())
            ? item.Value.GetString()!.Trim()
            : null;
}
