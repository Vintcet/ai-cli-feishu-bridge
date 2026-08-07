using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed class ActiveFeishuIntentHandler(
    BridgeHostOptions options,
    IBridgeProductionStoreOwner storeOwner,
    IBridgePersistentBusinessStateOwner businessStateOwner,
    IBridgeManagedRuntimeLaunchCoordinator runtimeLaunches,
    IBridgeRuntimeCommandGateway runtimeCommands,
    IFeishuGateway gateway,
    IFeishuCardRenderer renderer,
    ActiveFeishuPromptCoordinator prompts) : IBridgeFeishuIntentHandler
{
    private const int MaximumListedSessions = 50;
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
            FeishuIntentTypes.MessagePrompt => await prompts.HandleAsync(
                intent,
                store,
                cancellationToken),
            FeishuIntentTypes.CommandMenu => await PresentCardAsync(
                intent,
                renderer.CommandMenu(),
                "已打开命令菜单。",
                cancellationToken),
            FeishuIntentTypes.CommandNew => await PresentCardAsync(
                intent,
                renderer.RuntimeSelection(
                    store.Settings.WorkspaceRoot,
                    new(
                        Guid.NewGuid().ToString("N"),
                        intent.MessageId,
                        intent.ChatId)),
                "请选择运行环境。",
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
            FeishuIntentTypes.CommandAliases => await RespondTextAsync(
                intent,
                AliasesText(store.Sessions),
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

    private FeishuCallbackResult HandleRuntimeNewSelect(
        FeishuIntent intent,
        SettingsStoreDocument settings)
    {
        if (!TryRuntimeNewContext(intent, out var runtime, out var context))
        {
            return new("error", "新建会话卡片参数不完整。");
        }
        if (NewFlowState(context.FlowId) is not null)
        {
            return new("warning", "这次新建操作已经处理或失效。");
        }
        return new(
            "info",
            $"已选择 {RuntimeDisplayName(runtime)}，请填写项目名。",
            renderer.RuntimeProjectForm(runtime, settings.WorkspaceRoot, context));
    }

    private FeishuCallbackResult HandleRuntimeNewCancel(FeishuIntent intent)
    {
        if (!TryRuntimeNewContext(intent, out var runtime, out var context))
        {
            return new("error", "新建会话卡片参数不完整。");
        }

        var cancellation = RememberCancellation(context.FlowId);
        if (cancellation is RuntimeNewCancellationResult.TooLate)
        {
            return new("warning", "启动请求已经提交，不能再取消。");
        }
        if (cancellation is RuntimeNewCancellationResult.CapacityReached)
        {
            return new("warning", "当前新建请求较多，请稍后重试。");
        }
        return new(
            cancellation is RuntimeNewCancellationResult.AlreadyCancelled
                ? "info"
                : "success",
            "已取消新建会话。",
            renderer.RuntimeLaunchCancelled(runtime));
    }

    private async Task<FeishuCallbackResult> HandleRuntimeNewSubmitAsync(
        FeishuIntent intent,
        SettingsStoreDocument settings,
        CancellationToken cancellationToken)
    {
        if (!TryRuntimeNewContext(intent, out var runtime, out var context))
        {
            return new("error", "新建会话卡片参数不完整。");
        }
        var existingState = NewFlowState(context.FlowId);
        if (existingState is RuntimeNewFlowState.Cancelled)
        {
            return new("warning", "这次新建操作已经取消。");
        }
        if (existingState is not null)
        {
            return new("warning", "启动请求已经提交，请勿重复点击。");
        }
        var suppliedProjectName = intent.Parameters?.GetValueOrDefault(
            "form.project_name");
        if (string.IsNullOrWhiteSpace(suppliedProjectName))
        {
            return new("error", "请输入项目名。");
        }
        var projectName = BridgeWorkspaceProjectDirectory.NormalizeAndValidateName(
            suppliedProjectName,
            out var validationError);
        if (projectName is null)
        {
            return new("error", $"项目名不正确：{validationError}");
        }
        if (string.IsNullOrWhiteSpace(settings.WorkspaceRoot))
        {
            return new(
                "error",
                "尚未设置默认工作区，请先在电脑端“设置”中选择。");
        }

        var begin = BeginSubmission(context.FlowId);
        if (begin is RuntimeNewSubmissionResult.AlreadyCancelled)
        {
            return new("warning", "这次新建操作已经取消。");
        }
        if (begin is RuntimeNewSubmissionResult.AlreadySubmitted)
        {
            return new("warning", "启动请求已经提交，请勿重复点击。");
        }
        if (begin is RuntimeNewSubmissionResult.CapacityReached)
        {
            return new("warning", "当前新建请求较多，请稍后重试。");
        }

        BridgePreparedProjectDirectory prepared;
        try
        {
            prepared = BridgeWorkspaceProjectDirectory.Prepare(
                settings.WorkspaceRoot,
                projectName);
        }
        catch (BridgeProjectDirectoryException error)
        {
            AbandonSubmission(context.FlowId);
            return new("error", $"项目目录不可用：{error.Message}");
        }

        try
        {
            var commandId = $"feishu-launch-{Guid.NewGuid():N}";
            await runtimeCommands.DispatchAsync(
                new()
                {
                    ProtocolVersion = BridgeProtocolVersion.Current,
                    Runtime = runtime,
                    Session = new RuntimeSessionReference
                    {
                        ExternalId = $"launch-{Guid.NewGuid():N}",
                        Cwd = prepared.Cwd,
                    },
                    TraceId = intent.TraceId,
                    CorrelationId = context.FlowId,
                    CommandId = commandId,
                    CommandType = RuntimeCommandTypes.SessionLaunch,
                    CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                    Payload = JsonSerializer.SerializeToElement(new
                    {
                        cwd = prepared.Cwd,
                        elevated = false,
                    }),
                },
                cancellationToken);
        }
        catch
        {
            AbandonSubmission(context.FlowId);
            BridgeWorkspaceProjectDirectory.Rollback(prepared);
            throw;
        }

        CompleteSubmission(context.FlowId);
        return new(
            "success",
            $"已提交 {RuntimeDisplayName(runtime)} 启动请求。",
            renderer.RuntimeLaunchSubmitted(
                runtime,
                projectName,
                prepared.WorkspaceRoot));
    }

    private async Task<FeishuCallbackResult?> RejectUnboundAsync(
        FeishuIntent intent,
        BindingStoreDocument bindings,
        CancellationToken cancellationToken)
    {
        var message = string.IsNullOrWhiteSpace(bindings.OwnerOpenId)
            ? "飞书连接正常，但 C# Host 尚未取得管理员绑定。"
            : "飞书连接正常，但这个助手只允许已设置的管理员账号操作。";
        if (IsCardAction(intent))
        {
            return new("warning", message);
        }
        await SendTextWithFallbackAsync(intent, message, cancellationToken);
        return null;
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

    private async Task<FeishuCallbackResult?> RespondTextAsync(
        FeishuIntent intent,
        string text,
        CancellationToken cancellationToken)
    {
        await SendTextWithFallbackAsync(intent, text, cancellationToken);
        return IsCardAction(intent)
            ? new("success", "结果已发送到当前会话。")
            : null;
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
            : $"默认工作区：{settings.WorkspaceRoot}\n新建命令：/新建";

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
        var aliases = document.Sessions.Values
            .Where(session => !string.Equals(
                session.Status,
                SessionStatuses.Ended,
                StringComparison.Ordinal))
            .Select(session => (Session: session, Alias: ExtensionString(session, "alias")))
            .Where(item => item.Alias is not null)
            .OrderBy(item => item.Alias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Session.SessionId, StringComparer.Ordinal)
            .ToArray();
        if (aliases.Length == 0)
        {
            return "当前活跃会话没有设置别名。请在电脑端的会话列表中设置。";
        }
        return "会话别名：\n" + string.Join(
            '\n',
            aliases.Select(item =>
                $"@{item.Alias} -> {SessionLabel(item.Session)}"));
    }

    private static string HelpText() =>
        "一级命令：\n/新建 - 新建会话\n/会话 - 会话管理\n/状态 - 查看状态\n" +
        "/工作区 - 查看工作区\n/别名 - 会话别名\n/帮助 - 全部功能\n\n" +
        "发送 /新建 后，从卡片选择 Codex、Claude Code 或 OpenCode。";

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

    private static string ShortId(string sessionId) =>
        sessionId.Length <= 8 ? sessionId : sessionId[^8..];

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
