using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed class ActiveFeishuPromptCoordinator(
    IBridgeProductionStoreOwner storeOwner,
    IBridgePersistentBusinessStateOwner businessStateOwner,
    IBridgeRuntimeCommandGateway runtimeCommands,
    IFeishuGateway gateway)
{
    private const int MaximumDirectiveDepth = 3;
    private static readonly Regex QueueDirective = new(
        @"^(?:排队|/queue|queue)\s+([\s\S]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FileReturnDirective = new(
        @"^(?:发文件|/sendfile|sendfile)\s+([\s\S]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ExplicitShortTarget = new(
        @"^#([a-zA-Z0-9]{4,32})\s+([\s\S]+)$",
        RegexOptions.CultureInvariant);
    private static readonly Regex ExplicitAliasTarget = new(
        @"^@([^\s@#]+)\s+([\s\S]+)$",
        RegexOptions.CultureInvariant);

    public async Task<FeishuCallbackResult?> HandleAsync(
        FeishuIntent intent,
        NodeStoreSnapshot store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(store);
        cancellationToken.ThrowIfCancellationRequested();

        if (intent.Attachments is { Count: > 0 })
        {
            await RejectAsync(intent, null, "附件处理尚未迁移到 C# Host。", cancellationToken);
            return null;
        }

        var leading = ParseDirectives(intent.Text, MaximumDirectiveDepth);
        if (leading.FileReturnRequested || IsFileReturnDirective(leading.Prompt))
        {
            await RejectAsync(intent, null, "文件回传尚未迁移到 C# Host。", cancellationToken);
            return null;
        }

        var quotedRoute = QuotedRoute(intent, store.Routes);
        if (quotedRoute is not null && IsInteractiveRoute(quotedRoute))
        {
            await RejectAsync(
                intent,
                Session(store, quotedRoute.SessionId),
                "引用的审批或问答交互尚未迁移，请使用原卡片处理。",
                cancellationToken);
            return null;
        }

        var activeSessions = store.Sessions.Sessions.Values
            .Where(IsActive)
            .ToArray();
        var explicitTarget = ParseExplicitTarget(leading.Prompt);
        var prompt = leading.Prompt;
        SessionStoreRecord? target;
        if (!string.Equals(intent.ChatType, "p2p", StringComparison.Ordinal))
        {
            var groupMatches = store.Sessions.Sessions.Values
                .Where(session => string.Equals(
                    ExtensionString(session, "feishuChatId"),
                    intent.ChatId,
                    StringComparison.Ordinal))
                .ToArray();
            if (groupMatches.Length != 1)
            {
                await RejectAsync(
                    intent,
                    null,
                    groupMatches.Length == 0
                        ? "当前群未绑定会话。"
                        : "当前群绑定了多个会话，无法确定目标。",
                    cancellationToken);
                return null;
            }
            target = groupMatches[0];
            if (explicitTarget is not null)
            {
                prompt = explicitTarget.Prompt;
            }
        }
        else if (explicitTarget is not null)
        {
            var matches = explicitTarget.Kind is ExplicitTargetKind.ShortId
                ? activeSessions.Where(session => MatchesShortId(
                    session,
                    explicitTarget.Token)).ToArray()
                : activeSessions.Where(session => MatchesAlias(
                    session,
                    explicitTarget.Token)).ToArray();
            if (matches.Length != 1)
            {
                var address = explicitTarget.Kind is ExplicitTargetKind.ShortId
                    ? $"#{explicitTarget.Token}"
                    : $"@{explicitTarget.Token}";
                var reason = matches.Length == 0
                    ? $"没有找到 {address} 对应的活跃会话。"
                    : explicitTarget.Kind is ExplicitTargetKind.ShortId
                        ? $"{address} 匹配到多个会话。"
                        : $"{address} 不是唯一别名。";
                await RejectAsync(intent, null, reason, cancellationToken);
                return null;
            }
            target = matches[0];
            prompt = explicitTarget.Prompt;
        }
        else if (quotedRoute is not null)
        {
            target = activeSessions.SingleOrDefault(session => string.Equals(
                session.SessionId,
                quotedRoute.SessionId,
                StringComparison.Ordinal));
        }
        else if (activeSessions.Length == 1)
        {
            target = activeSessions[0];
        }
        else
        {
            await RejectAsync(
                intent,
                null,
                activeSessions.Length == 0
                    ? "当前没有活跃会话。"
                    : "有多个活跃会话，请指定目标。",
                cancellationToken);
            return null;
        }

        if (target is null)
        {
            await RejectAsync(intent, null, "对应会话不可用。", cancellationToken);
            return null;
        }

        var nested = ParseDirectives(
            prompt,
            MaximumDirectiveDepth - leading.ConsumedDirectives);
        prompt = nested.Prompt;
        var queueRequested = leading.QueueRequested || nested.QueueRequested;
        if (nested.FileReturnRequested || IsFileReturnDirective(prompt))
        {
            await RejectAsync(intent, target, "文件回传尚未迁移到 C# Host。", cancellationToken);
            return null;
        }
        if (string.IsNullOrWhiteSpace(prompt))
        {
            await RejectAsync(intent, target, "内容为空。", cancellationToken);
            return null;
        }

        var runtime = Runtime(target);
        if (!RuntimeNames.All.Contains(runtime))
        {
            await RejectAsync(intent, target, "会话运行时不可用。", cancellationToken);
            return null;
        }
        var business = businessStateOwner.Snapshot;
        if (!business.Initialized)
        {
            throw new InvalidOperationException("Active Host 业务状态尚未初始化。");
        }
        var pendingReason = PendingReason(target, store, business);
        if (pendingReason is not null)
        {
            await RejectAsync(intent, target, pendingReason, cancellationToken);
            return null;
        }

        var runtimeSession = new RuntimeSession(target.SessionId, target.Cwd);
        var ready = runtimeCommands.IsReady(runtime, runtimeSession);
        var managedByAssistant = ExtensionBoolean(target, "managedByAssistant");
        var groupSession = !string.Equals(intent.ChatType, "p2p", StringComparison.Ordinal);
        var canResume = groupSession &&
            IsActive(target) &&
            managedByAssistant &&
            !HasClientProcess(target);
        if (!ready && canResume)
        {
            var resumed = await TryDispatchAsync(
                intent,
                target,
                RuntimeCommandTypes.SessionResume,
                JsonSerializer.SerializeToElement(new { prompt }),
                cancellationToken);
            if (!resumed)
            {
                return null;
            }
            var messageId = await RespondAsync(
                intent,
                $"{RuntimeDisplayName(runtime)} 窗口已关闭，正在请求电脑端自动恢复；" +
                    "这条消息会在窗口就绪后发送。",
                cancellationToken);
            await RememberAcknowledgementAsync(
                messageId,
                intent.ChatId,
                target.SessionId,
                cancellationToken);
            return null;
        }

        if (runtime is not RuntimeNames.OpenCode &&
            string.IsNullOrWhiteSpace(ExtensionString(target, "managedTerminalId")))
        {
            await RejectAsync(
                intent,
                target,
                "这个窗口不是由 AI CLI 飞书助手打开，不能从飞书回复。请回到原窗口继续。",
                cancellationToken);
            return null;
        }
        if (!ready)
        {
            await RejectAsync(
                intent,
                target,
                runtime is RuntimeNames.OpenCode
                    ? "OpenCode 窗口未连接。"
                    : groupSession && !IsActive(target)
                        ? "对应窗口已关闭。"
                        : "窗口尚未就绪。",
                cancellationToken);
            return null;
        }

        var status = EffectiveStatus(target, business);
        var mode = queueRequested &&
            string.Equals(status, SessionStatuses.Running, StringComparison.Ordinal) &&
            runtime is not RuntimeNames.OpenCode
                ? "queue"
                : "steer";
        var dispatched = await TryDispatchAsync(
            intent,
            target,
            RuntimeCommandTypes.PromptSend,
            JsonSerializer.SerializeToElement(new { prompt, mode }),
            cancellationToken);
        if (!dispatched)
        {
            return null;
        }

        var acknowledgementId = await RespondAsync(
            intent,
            $"{RuntimeDisplayName(runtime)} 已接收。",
            cancellationToken);
        await RememberAcknowledgementAsync(
            acknowledgementId,
            intent.ChatId,
            target.SessionId,
            cancellationToken);
        return null;
    }

    private async Task<bool> TryDispatchAsync(
        FeishuIntent intent,
        SessionStoreRecord target,
        string commandType,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        try
        {
            await runtimeCommands.DispatchAsync(
                new()
                {
                    ProtocolVersion = BridgeProtocolVersion.Current,
                    Runtime = Runtime(target),
                    Session = new RuntimeSessionReference
                    {
                        ExternalId = target.SessionId,
                        Cwd = target.Cwd,
                    },
                    TraceId = intent.TraceId,
                    CorrelationId = intent.EventId,
                    CommandId = $"feishu-prompt-{Guid.NewGuid():N}",
                    CommandType = commandType,
                    CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                    Payload = payload,
                },
                cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await RejectAsync(
                intent,
                target,
                "暂时无法把消息交给助手，请稍后重试。",
                cancellationToken);
            return false;
        }
    }

    private async Task RejectAsync(
        FeishuIntent intent,
        SessionStoreRecord? target,
        string reason,
        CancellationToken cancellationToken) =>
        _ = await RespondAsync(
            intent,
            $"{RuntimeDisplayName(target?.Runtime)} 未接收：{reason}",
            cancellationToken);

    private async Task<string> RespondAsync(
        FeishuIntent intent,
        string text,
        CancellationToken cancellationToken)
    {
        try
        {
            return await gateway.ReplyTextAsync(
                intent.MessageId,
                text,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return await gateway.SendTextAsync(intent.ChatId, text, cancellationToken);
        }
    }

    private async Task RememberAcknowledgementAsync(
        string messageId,
        string chatId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return;
        }
        var route = new MessageRouteStoreRecord
        {
            MessageId = messageId,
            SessionId = sessionId,
            ChatId = chatId,
            Kind = "resume_ack",
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
        };
        await storeOwner.UpdateAsync(
            store => store with { Routes = AddRoute(store.Routes, route) },
            cancellationToken);
    }

    private static RouteStoreDocument AddRoute(
        RouteStoreDocument current,
        MessageRouteStoreRecord route)
    {
        var messages = new Dictionary<string, MessageRouteStoreRecord>(
            current.Messages,
            StringComparer.Ordinal)
        {
            [route.MessageId] = route,
        };
        return new()
        {
            Messages = messages,
            ProcessedInbound = new Dictionary<string, string>(
                current.ProcessedInbound,
                StringComparer.Ordinal),
            ExtensionData = CloneExtensions(current.ExtensionData),
        };
    }

    private static string? PendingReason(
        SessionStoreRecord target,
        NodeStoreSnapshot store,
        BridgeBusinessStateSnapshot business)
    {
        var status = EffectiveStatus(target, business);
        var pendingApproval = status is SessionStatuses.PendingApproval or
                SessionStatuses.LocalApproval ||
            store.Approvals.Requests.Values.Any(approval =>
                string.Equals(approval.SessionId, target.SessionId, StringComparison.Ordinal) &&
                string.Equals(approval.Status, ApprovalStatuses.Pending, StringComparison.Ordinal)) ||
            business.Approvals.Requests.Values.Any(approval =>
                string.Equals(approval.SessionId, target.SessionId, StringComparison.Ordinal) &&
                string.Equals(approval.Status, ApprovalStatuses.Pending, StringComparison.Ordinal));
        if (pendingApproval)
        {
            return "请先处理待审批操作。";
        }
        var pendingInput = status is SessionStatuses.PendingInput ||
            business.Inputs.Requests.Values.Any(input =>
                string.Equals(input.SessionId, target.SessionId, StringComparison.Ordinal) &&
                string.Equals(input.Status, InputRequestStatuses.Pending, StringComparison.Ordinal));
        return pendingInput ? "请先回答待补充问题。" : null;
    }

    private static string EffectiveStatus(
        SessionStoreRecord target,
        BridgeBusinessStateSnapshot business) =>
        business.Sessions.Sessions.TryGetValue(target.SessionId, out var session)
            ? session.Status
            : target.Status;

    private static PromptDirectives ParseDirectives(string? text, int maximumDepth)
    {
        var prompt = text?.Trim() ?? string.Empty;
        var queue = false;
        var fileReturn = false;
        var consumed = 0;
        while (consumed < Math.Max(0, maximumDepth))
        {
            var queueMatch = QueueDirective.Match(prompt);
            if (queueMatch.Success)
            {
                queue = true;
                prompt = queueMatch.Groups[1].Value.Trim();
                consumed++;
                continue;
            }
            var fileMatch = FileReturnDirective.Match(prompt);
            if (fileMatch.Success)
            {
                fileReturn = true;
                prompt = fileMatch.Groups[1].Value.Trim();
                consumed++;
                continue;
            }
            break;
        }
        return new(prompt, queue, fileReturn, consumed);
    }

    private static ExplicitTarget? ParseExplicitTarget(string text)
    {
        var shortMatch = ExplicitShortTarget.Match(text);
        if (shortMatch.Success)
        {
            return new(
                ExplicitTargetKind.ShortId,
                shortMatch.Groups[1].Value.ToLowerInvariant(),
                shortMatch.Groups[2].Value.Trim());
        }
        var aliasMatch = ExplicitAliasTarget.Match(text);
        return aliasMatch.Success
            ? new(
                ExplicitTargetKind.Alias,
                aliasMatch.Groups[1].Value,
                aliasMatch.Groups[2].Value.Trim())
            : null;
    }

    private static MessageRouteStoreRecord? QuotedRoute(
        FeishuIntent intent,
        RouteStoreDocument routes)
    {
        var parentMessageId = intent.Parameters?.GetValueOrDefault("parentMessageId");
        return !string.IsNullOrWhiteSpace(parentMessageId) &&
            routes.Messages.TryGetValue(parentMessageId, out var route)
                ? route
                : null;
    }

    private static bool IsInteractiveRoute(MessageRouteStoreRecord route) =>
        !string.IsNullOrWhiteSpace(route.RequestId) ||
        string.Equals(route.Kind, "approval", StringComparison.Ordinal) ||
        string.Equals(route.Kind, "input", StringComparison.Ordinal);

    private static SessionStoreRecord? Session(NodeStoreSnapshot store, string sessionId) =>
        store.Sessions.Sessions.GetValueOrDefault(sessionId);

    private static bool IsActive(SessionStoreRecord session) =>
        !string.Equals(session.Status, SessionStatuses.Ended, StringComparison.Ordinal);

    private static bool MatchesShortId(SessionStoreRecord session, string token)
    {
        var normalized = new string(session.SessionId
            .Where(character => character is >= 'a' and <= 'z' or
                >= 'A' and <= 'Z' or >= '0' and <= '9')
            .ToArray());
        return normalized.EndsWith(token, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesAlias(SessionStoreRecord session, string alias)
    {
        var stored = ExtensionString(session, "alias");
        return stored is not null && string.Equals(
            stored.Normalize(NormalizationForm.FormC),
            alias.Trim().Normalize(NormalizationForm.FormC),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFileReturnDirective(string text) =>
        FileReturnDirective.IsMatch(text) ||
        string.Equals(text, "发文件", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(text, "/sendfile", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(text, "sendfile", StringComparison.OrdinalIgnoreCase);

    private static bool HasClientProcess(SessionStoreRecord session) =>
        session.ExtensionData is not null &&
        session.ExtensionData.TryGetValue("clientProcessId", out var value) &&
        value.ValueKind is JsonValueKind.Number;

    private static bool ExtensionBoolean(ExtensibleStoreObject value, string name) =>
        value.ExtensionData is not null &&
        value.ExtensionData.TryGetValue(name, out var property) &&
        property.ValueKind is JsonValueKind.True;

    private static string? ExtensionString(ExtensibleStoreObject value, string name) =>
        value.ExtensionData is not null &&
        value.ExtensionData.TryGetValue(name, out var property) &&
        property.ValueKind is JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!.Trim()
            : null;

    private static Dictionary<string, JsonElement>? CloneExtensions(
        Dictionary<string, JsonElement>? extensions) =>
        extensions?.ToDictionary(
            item => item.Key,
            item => item.Value.Clone(),
            StringComparer.Ordinal);

    private static string Runtime(SessionStoreRecord session) =>
        string.IsNullOrWhiteSpace(session.Runtime)
            ? RuntimeNames.Codex
            : session.Runtime;

    private static string RuntimeDisplayName(string? runtime) => runtime switch
    {
        RuntimeNames.ClaudeCode => "Claude Code",
        RuntimeNames.OpenCode => "OpenCode",
        _ => "Codex",
    };

    private sealed record PromptDirectives(
        string Prompt,
        bool QueueRequested,
        bool FileReturnRequested,
        int ConsumedDirectives);

    private sealed record ExplicitTarget(
        ExplicitTargetKind Kind,
        string Token,
        string Prompt);

    private enum ExplicitTargetKind
    {
        ShortId,
        Alias,
    }
}
