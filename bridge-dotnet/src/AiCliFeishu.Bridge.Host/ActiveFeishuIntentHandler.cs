using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed partial class ActiveFeishuIntentHandler(
    BridgeHostOptions options,
    IBridgeProductionStoreOwner storeOwner,
    IBridgePersistentBusinessStateOwner businessStateOwner,
    IBridgeActiveSessionAliasStateOwner sessionAliases,
    IBridgeActiveSessionGroupStateOwner sessionGroups,
    IBridgeManagedRuntimeLaunchCoordinator runtimeLaunches,
    ActiveRuntimeLaunchNotificationCoordinator launchNotifications,
    IBridgeRuntimeCommandGateway runtimeCommands,
    IBridgeActiveRuntimeRetryCoordinator runtimeRetries,
    IFeishuGateway gateway,
    IFeishuCardRenderer renderer,
    ActiveFeishuPromptCoordinator prompts,
    ActiveFeishuApprovalCoordinator approvals,
    ActiveFeishuInputCoordinator inputs) : IBridgeFeishuIntentHandler
{
    private const int MaximumListedSessions = 50;
    private const int MaximumListedAliases = 20;
    private const int MaximumRememberedNewFlows = 500;
    private readonly object newFlowSync = new();
    private readonly Dictionary<string, RuntimeNewFlowEntry> newFlows =
        new(StringComparer.Ordinal);
    private readonly LinkedList<string> newFlowOrder = [];
    private static readonly IReadOnlySet<string> supportedIntentTypes =
        new HashSet<string>(
        [
            FeishuIntentTypes.CommandMenu,
            FeishuIntentTypes.CommandNew,
            FeishuIntentTypes.CommandWorkspace,
            FeishuIntentTypes.CommandStatus,
            FeishuIntentTypes.CommandSessions,
            FeishuIntentTypes.CommandAliases,
            FeishuIntentTypes.CommandHelp,
            FeishuIntentTypes.RuntimeNewSelect,
            FeishuIntentTypes.RuntimeNewSubmit,
            FeishuIntentTypes.RuntimeNewCancel,
            FeishuIntentTypes.MessagePrompt,
            FeishuIntentTypes.ApprovalResolve,
            FeishuIntentTypes.ApprovalDeferToLocal,
            FeishuIntentTypes.InputAnswer,
            FeishuIntentTypes.InputToggle,
            FeishuIntentTypes.InputSubmit,
            FeishuIntentTypes.InputDeferToLocal,
            FeishuIntentTypes.RetryStop,
        ],
        StringComparer.Ordinal);

    public async Task<FeishuCallbackResult?> HandleAsync(
        FeishuIntent intent,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(intent);

        var store = await storeOwner.ReadAsync(cancellationToken);
        if (TryParseBindingCommand(intent, out var pairingCode))
        {
            return await BindAsync(intent, pairingCode, cancellationToken);
        }
        if (IsUnbindCommand(intent))
        {
            return await UnbindAsync(intent, cancellationToken);
        }
        if (!IsBoundOwner(store.Bindings, intent.OperatorOpenId))
        {
            return await RejectUnboundAsync(intent, store.Bindings, cancellationToken);
        }
        if (!supportedIntentTypes.Contains(intent.IntentType))
        {
            throw new NotSupportedException(
                $"Active Host 尚未迁移飞书标准意图 {intent.IntentType}。");
        }

        return intent.IntentType switch
        {
            FeishuIntentTypes.MessagePrompt => await HandleMessagePromptAsync(
                intent,
                store,
                cancellationToken),
            FeishuIntentTypes.ApprovalResolve or
            FeishuIntentTypes.ApprovalDeferToLocal => await approvals.HandleAsync(
                intent,
                store,
                cancellationToken),
            FeishuIntentTypes.InputAnswer or
            FeishuIntentTypes.InputToggle or
            FeishuIntentTypes.InputSubmit or
            FeishuIntentTypes.InputDeferToLocal => await inputs.HandleAsync(
                intent,
                store,
                cancellationToken),
            FeishuIntentTypes.RetryStop => await StopRetryAsync(
                intent,
                cancellationToken),
            FeishuIntentTypes.CommandMenu => await PresentCardAsync(
                intent,
                renderer.CommandMenu(),
                "已打开命令菜单。",
                cancellationToken),
            FeishuIntentTypes.CommandNew => await HandleCommandNewAsync(
                intent,
                store.Settings,
                cancellationToken),
            FeishuIntentTypes.CommandWorkspace => await RespondTextAsync(
                intent,
                WorkspaceText(store.Settings),
                cancellationToken),
            FeishuIntentTypes.CommandStatus => await RespondTextAsync(
                intent,
                StatusText(businessStateOwner.Snapshot, runtimeLaunches.Snapshot),
                cancellationToken),
            FeishuIntentTypes.CommandSessions => await RespondTextAsync(
                intent,
                SessionsText(store.Sessions),
                cancellationToken),
            FeishuIntentTypes.CommandAliases => await HandleAliasesAsync(
                intent,
                store,
                cancellationToken),
            FeishuIntentTypes.CommandHelp => await RespondTextAsync(
                intent,
                HelpText(),
                cancellationToken),
            FeishuIntentTypes.RuntimeNewSelect => HandleRuntimeNewSelect(
                intent,
                store.Settings),
            FeishuIntentTypes.RuntimeNewCancel => HandleRuntimeNewCancel(intent),
            FeishuIntentTypes.RuntimeNewSubmit => await HandleRuntimeNewSubmitAsync(
                intent,
                store.Settings,
                cancellationToken),
            _ => throw new InvalidOperationException("飞书全局意图分派不完整。"),
        };
    }

    private async Task<FeishuCallbackResult?> HandleMessagePromptAsync(
        FeishuIntent intent,
        BridgeStoreSnapshot store,
        CancellationToken cancellationToken)
    {
        if (await inputs.TryHandleQuotedReplyAsync(
                intent,
                store,
                cancellationToken))
        {
            return null;
        }

        var approval = await approvals.TryHandleQuotedReplyAsync(
            intent,
            store,
            cancellationToken);
        if (approval is not null)
        {
            await RespondTextAsync(intent, approval.ToastContent, cancellationToken);
            return null;
        }

        return await prompts.HandleAsync(intent, store, cancellationToken);
    }

    private async Task<FeishuCallbackResult?> PresentCardAsync(
        FeishuIntent intent,
        FeishuCardView card,
        string toast,
        CancellationToken cancellationToken)
    {
        if (IsCardAction(intent))
        {
            return new("success", toast, card);
        }
        await gateway.SendCardAsync(
            intent.ChatId,
            card,
            $"feishu-intent:{intent.EventId}",
            cancellationToken);
        return null;
    }

    private async Task<FeishuCallbackResult?> HandleAliasesAsync(
        FeishuIntent intent,
        BridgeStoreSnapshot store,
        CancellationToken cancellationToken)
    {
        var text = intent.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text) ||
            text == "/" ||
            FeishuAliasCommandParser.IsListCommand(text))
        {
            return await RespondTextAsync(
                intent,
                AliasesText(store.Sessions),
                cancellationToken);
        }

        var command = FeishuAliasCommandParser.Parse(text);
        if (command is null || command.TargetKind is null || command.Target is null)
        {
            return await RespondTextAsync(
                intent,
                FeishuAliasCommandParser.Usage(),
                cancellationToken);
        }
        if (!string.Equals(intent.ChatType, "p2p", StringComparison.Ordinal))
        {
            return await RespondTextAsync(
                intent,
                "会话别名管理只能在机器人私聊中使用。",
                cancellationToken);
        }

        var activeSessions = store.Sessions.Sessions.Values
            .Where(IsActive)
            .ToArray();
        var matches = command.TargetKind is FeishuAliasTargetKind.ShortId
            ? activeSessions
                .Where(session => MatchesShortId(session, command.Target))
                .ToArray()
            : activeSessions
                .Where(session => MatchesAlias(session, command.Target))
                .ToArray();
        var address = command.TargetKind is FeishuAliasTargetKind.ShortId
            ? $"#{command.Target}"
            : $"@{command.Target}";
        if (matches.Length != 1)
        {
            return await RespondTextAsync(
                intent,
                matches.Length == 0
                    ? $"没有找到 {address} 对应的活跃会话。发送“会话”查看列表。"
                    : $"{address} 匹配到多个会话，请换用完整短 ID。",
                cancellationToken);
        }

        var session = matches[0];
        if (command.Alias is null)
        {
            var currentAlias = ExtensionString(session, "alias");
            return await RespondTextAsync(
                intent,
                currentAlias is not null
                    ? $"会话 {ProjectLabel(session)} 的别名是 @{currentAlias}。"
                    : $"会话 {ProjectLabel(session)} 尚未设置别名。",
                cancellationToken);
        }

        var clear = IsAliasClearWord(command.Alias);
        var update = await sessionAliases.UpdateSessionAliasAsync(
            session.SessionId,
            clear ? null : command.Alias,
            cancellationToken);
        if (update.Conflict is not null)
        {
            return await RespondTextAsync(
                intent,
                $"别名 @{SessionAliasRules.Normalize(command.Alias)} 已被会话 " +
                $"{ProjectLabel(update.Conflict)} 使用。",
                cancellationToken);
        }
        if (!update.Succeeded || update.Session is null)
        {
            return await RespondTextAsync(
                intent,
                update.Error ?? "设置别名失败。",
                cancellationToken);
        }

        var updatedAlias = ExtensionString(update.Session, "alias");
        var groupNameFailure = await SynchronizeSessionGroupNameAsync(
            update.Session,
            cancellationToken);
        var response = updatedAlias is not null
            ? $"已将 {ProjectLabel(update.Session)} 的别名设为 @{updatedAlias}。" +
              $"以后可发送“@{updatedAlias} 回复内容”。"
            : $"已清除 {ProjectLabel(update.Session)} 的别名。";
        if (groupNameFailure is not null)
        {
            response += $"\n\n别名已保存，但飞书群名同步失败：{groupNameFailure}";
        }
        return await RespondTextAsync(
            intent,
            response,
            cancellationToken);
    }

    private async Task<string?> SynchronizeSessionGroupNameAsync(
        SessionStoreRecord session,
        CancellationToken cancellationToken) =>
        await SessionGroupNameSynchronizer.SynchronizeAsync(
            session,
            sessionGroups,
            gateway,
            cancellationToken);

    private async Task<FeishuCallbackResult?> RespondTextAsync(
        FeishuIntent intent,
        string text,
        CancellationToken cancellationToken)
    {
        if (IsCardAction(intent))
        {
            return new(
                "success",
                "结果已发送到当前会话。",
                AfterAcknowledged: acknowledgedCancellation =>
                    SendTextWithFallbackAsync(
                        intent,
                        text,
                        acknowledgedCancellation));
        }
        await SendTextWithFallbackAsync(intent, text, cancellationToken);
        return null;
    }

    private async Task SendTextWithFallbackAsync(
        FeishuIntent intent,
        string text,
        CancellationToken cancellationToken)
    {
        try
        {
            await gateway.ReplyTextAsync(intent.MessageId, text, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await gateway.SendTextAsync(intent.ChatId, text, cancellationToken);
        }
    }

    private static string WorkspaceText(SettingsStoreDocument settings) =>
        string.IsNullOrWhiteSpace(settings.WorkspaceRoot)
            ? "尚未设置默认工作区。请在电脑端“设置”中选择。"
            : $"默认工作区：{settings.WorkspaceRoot}\n" +
              "新建命令示例：新建 codex 我的项目";

    private static string StatusText(
        BridgeBusinessStateSnapshot business,
        BridgeManagedRuntimeLifecycleSnapshot launches)
    {
        if (!business.Initialized)
        {
            throw new InvalidOperationException("Active Host 业务状态尚未初始化。");
        }
        var activeSessions = business.Sessions.Sessions.Values.Count(session =>
            session.Status != SessionStatuses.Ended);
        var pendingApprovals = business.Approvals.Requests.Values.Count(approval =>
            approval.Status == ApprovalStatuses.Pending);
        var pendingInputs = business.Inputs.Requests.Values.Count(input =>
            input.Status == InputRequestStatuses.Pending);
        return $"飞书桥接在线，当前账号已绑定。活跃会话 {activeSessions} 个，" +
            $"待审批 {pendingApprovals} 个，待补充 {pendingInputs} 个，" +
            $"排队 {launches.QueuedPrompts} 条。";
    }

    private static string SessionsText(SessionStoreDocument document)
    {
        var sessions = document.Sessions.Values
            .Where(session => !string.Equals(
                session.Status,
                SessionStatuses.Ended,
                StringComparison.Ordinal))
            .OrderByDescending(session => Timestamp(session.LastSeenAt))
            .ThenBy(session => session.SessionId, StringComparer.Ordinal)
            .Take(MaximumListedSessions)
            .ToArray();
        if (sessions.Length == 0)
        {
            return "当前没有活跃会话。";
        }

        var text = new StringBuilder("活跃会话：");
        for (var index = 0; index < sessions.Length; index++)
        {
            var session = sessions[index];
            text.Append('\n')
                .Append(index + 1)
                .Append(". ")
                .Append(SessionLabel(session))
                .Append(" [")
                .Append(RuntimeDisplayName(session.Runtime))
                .Append(" / ")
                .Append(session.Status)
                .Append("]\n   ")
                .Append(session.Cwd);
        }
        return text.ToString();
    }

    private static string AliasesText(SessionStoreDocument document)
    {
        var sessions = document.Sessions.Values
            .Where(IsActive)
            .OrderByDescending(session => Timestamp(session.LastSeenAt))
            .ThenBy(session => session.SessionId, StringComparer.Ordinal)
            .Take(MaximumListedAliases)
            .ToArray();
        if (sessions.Length == 0)
        {
            return $"当前没有可设置别名的活跃会话。\n\n" +
                FeishuAliasCommandParser.Usage();
        }
        var lines = sessions.Select((session, index) =>
        {
            var alias = ExtensionString(session, "alias");
            var address = alias is null ? "（未设置）" : $"@{alias}";
            return $"{index + 1}. {address} · #{SessionShortId(session)} · " +
                ProjectName(session);
        });
        return "当前会话别名：\n" + string.Join(
            '\n',
            lines) + "\n\n" + FeishuAliasCommandParser.Usage();
    }

    private static string HelpText() =>
        "一级命令：\n/新建 - 新建会话\n/会话 - 会话管理\n/状态 - 查看状态\n" +
        "/工作区 - 查看工作区\n/别名 - 会话别名\n/帮助 - 全部功能\n\n" +
        "发送 /新建 后，从卡片选择 Codex、Claude Code 或 OpenCode。\n" +
        "也可以发送“新建 codex 项目名”，直接创建或打开项目。\n" +
        "在机器人私聊发送“别名 #短ID 名称”可设置会话别名。";

    private static bool IsBoundOwner(BindingStoreDocument bindings, string openId) =>
        !string.IsNullOrWhiteSpace(openId) &&
        string.Equals(bindings.OwnerOpenId, openId, StringComparison.Ordinal) &&
        bindings.Users.TryGetValue(openId, out var binding) &&
        string.Equals(binding.OpenId, openId, StringComparison.Ordinal);

    private static bool IsCardAction(FeishuIntent intent) =>
        string.Equals(intent.ChatType, "card", StringComparison.Ordinal);

    private static bool TryRuntimeNewContext(
        FeishuIntent intent,
        out string runtime,
        out FeishuRuntimeNewContext context)
    {
        var parameters = intent.Parameters;
        var flowId = ShortParameter(parameters, "flowId", 128);
        runtime = ShortParameter(parameters, "runtime", 32) ?? string.Empty;
        var valueChatId = ShortParameter(parameters, "chatId", 256);
        var sourceMessageId = ShortParameter(parameters, "sourceMessageId", 256) ??
            ShortValue(intent.MessageId, 256);
        var callbackChatId = ShortValue(intent.ChatId, 256);
        if (flowId is null ||
            !RuntimeNames.All.Contains(runtime) ||
            sourceMessageId is null ||
            callbackChatId is null ||
            valueChatId is not null &&
            !string.Equals(valueChatId, callbackChatId, StringComparison.Ordinal))
        {
            context = null!;
            return false;
        }
        context = new(flowId, sourceMessageId, callbackChatId);
        return true;
    }

    private static string? ShortParameter(
        IReadOnlyDictionary<string, string>? parameters,
        string name,
        int maximumLength) =>
        parameters?.TryGetValue(name, out var value) == true
            ? ShortValue(value, maximumLength)
            : null;

    private static string? ShortValue(string? value, int maximumLength)
    {
        var normalized = value?.Trim();
        return normalized is { Length: > 0 } && normalized.Length <= maximumLength
            ? normalized
            : null;
    }

    private RuntimeNewFlowState? NewFlowState(string flowId)
    {
        lock (newFlowSync)
        {
            return newFlows.GetValueOrDefault(flowId)?.State;
        }
    }

    private RuntimeNewCancellationResult RememberCancellation(string flowId)
    {
        lock (newFlowSync)
        {
            if (newFlows.TryGetValue(flowId, out var existing))
            {
                return existing.State is RuntimeNewFlowState.Cancelled
                    ? RuntimeNewCancellationResult.AlreadyCancelled
                    : RuntimeNewCancellationResult.TooLate;
            }
            if (!MakeFlowCapacityLocked())
            {
                return RuntimeNewCancellationResult.CapacityReached;
            }
            AddFlowLocked(flowId, RuntimeNewFlowState.Cancelled);
            return RuntimeNewCancellationResult.Cancelled;
        }
    }

    private RuntimeNewSubmissionResult BeginSubmission(string flowId)
    {
        lock (newFlowSync)
        {
            if (newFlows.TryGetValue(flowId, out var existing))
            {
                return existing.State is RuntimeNewFlowState.Cancelled
                    ? RuntimeNewSubmissionResult.AlreadyCancelled
                    : RuntimeNewSubmissionResult.AlreadySubmitted;
            }
            if (!MakeFlowCapacityLocked())
            {
                return RuntimeNewSubmissionResult.CapacityReached;
            }
            AddFlowLocked(flowId, RuntimeNewFlowState.Submitting);
            return RuntimeNewSubmissionResult.Started;
        }
    }

    private void CompleteSubmission(string flowId)
    {
        lock (newFlowSync)
        {
            if (newFlows.TryGetValue(flowId, out var entry) &&
                entry.State is RuntimeNewFlowState.Submitting)
            {
                entry.State = RuntimeNewFlowState.Submitted;
            }
        }
    }

    private void AbandonSubmission(string flowId)
    {
        lock (newFlowSync)
        {
            if (newFlows.TryGetValue(flowId, out var entry) &&
                entry.State is RuntimeNewFlowState.Submitting)
            {
                newFlows.Remove(flowId);
                newFlowOrder.Remove(entry.Node);
            }
        }
    }

    private bool MakeFlowCapacityLocked()
    {
        if (newFlows.Count < MaximumRememberedNewFlows)
        {
            return true;
        }
        var node = newFlowOrder.First;
        while (node is not null &&
            newFlows[node.Value].State is RuntimeNewFlowState.Submitting)
        {
            node = node.Next;
        }
        if (node is null)
        {
            return false;
        }
        newFlows.Remove(node.Value);
        newFlowOrder.Remove(node);
        return true;
    }

    private void AddFlowLocked(string flowId, RuntimeNewFlowState state)
    {
        var node = newFlowOrder.AddLast(flowId);
        newFlows.Add(flowId, new(state, node));
    }

    private static string SessionLabel(SessionStoreRecord session) =>
        ExtensionString(session, "alias") ??
        session.ProjectName ??
        session.ShortId ??
        ShortId(session.SessionId);

    private static string RuntimeDisplayName(string? runtime) => runtime switch
    {
        RuntimeNames.ClaudeCode => "Claude Code",
        RuntimeNames.OpenCode => "OpenCode",
        _ => "Codex",
    };

    private static string? ExtensionString(ExtensibleStoreObject value, string name) =>
        value.ExtensionData is not null &&
        value.ExtensionData.TryGetValue(name, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!.Trim()
            : null;

    private static int? ExtensionPositiveInt(
        ExtensibleStoreObject value,
        string name) =>
        value.ExtensionData is not null &&
        value.ExtensionData.TryGetValue(name, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt32(out var number) &&
        number > 0
            ? number
            : null;

    private static string ShortId(string sessionId) =>
        sessionId.Length <= 8 ? sessionId : sessionId[^8..];

    private static string ProjectLabel(SessionStoreRecord session) =>
        $"{ProjectName(session)} #{SessionShortId(session)}";

    private static string SessionShortId(SessionStoreRecord session) =>
        string.IsNullOrWhiteSpace(session.ShortId)
            ? ShortId(session.SessionId)
            : session.ShortId.Trim();

    private static string ProjectName(SessionStoreRecord session) =>
        session.ProjectName ??
        (string.IsNullOrWhiteSpace(session.Cwd) ? "未知项目" : session.Cwd);

    private static bool IsActive(SessionStoreRecord session) =>
        !string.Equals(session.Status, SessionStatuses.Ended, StringComparison.Ordinal);

    private static bool MatchesShortId(SessionStoreRecord session, string token)
    {
        var compact = new string(session.SessionId
            .Where(character => character is >= 'a' and <= 'z' or
                >= 'A' and <= 'Z' or
                >= '0' and <= '9')
            .ToArray());
        return compact.EndsWith(token, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesAlias(SessionStoreRecord session, string alias) =>
        ExtensionString(session, "alias") is { } stored &&
        string.Equals(
            SessionAliasRules.Key(stored),
            SessionAliasRules.Key(alias),
            StringComparison.Ordinal);

    private static bool IsAliasClearWord(string value) =>
        value.Trim().Equals("清除", StringComparison.Ordinal) ||
        value.Trim().Equals("删除", StringComparison.Ordinal) ||
        value.Trim().Equals("clear", StringComparison.OrdinalIgnoreCase) ||
        value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset Timestamp(string value) =>
        DateTimeOffset.TryParse(value, out var timestamp)
            ? timestamp
            : DateTimeOffset.MinValue;

    private void EnsureActive()
    {
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException(
                "飞书生产意图处理器只能用于 Active Host。");
        }
    }

    private sealed class RuntimeNewFlowEntry(
        RuntimeNewFlowState state,
        LinkedListNode<string> node)
    {
        public RuntimeNewFlowState State { get; set; } = state;

        public LinkedListNode<string> Node { get; } = node;
    }

    private enum RuntimeNewFlowState
    {
        Submitting,
        Submitted,
        Cancelled,
    }

    private enum RuntimeNewSubmissionResult
    {
        Started,
        AlreadySubmitted,
        AlreadyCancelled,
        CapacityReached,
    }

    private enum RuntimeNewCancellationResult
    {
        Cancelled,
        AlreadyCancelled,
        TooLate,
        CapacityReached,
    }
}
