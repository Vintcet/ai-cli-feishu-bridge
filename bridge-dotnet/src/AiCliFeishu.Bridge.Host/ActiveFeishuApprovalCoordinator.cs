using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed record BridgeLocalApprovalResolveResult(
    bool Ok,
    bool AlreadyResolved,
    string Resolution,
    string Message,
    string? Error = null);

internal sealed class ActiveFeishuApprovalCoordinator
{
    private static readonly TimeSpan LegacyCardPatchDelay =
        TimeSpan.FromMilliseconds(500);
    private readonly IBridgeActiveApprovalStateOwner stateOwner;
    private readonly Func<IBridgeRuntimeCommandGateway> runtimeCommands;
    private readonly FeishuInteractionCoordinator interactions;
    private readonly IFeishuCardRenderer renderer;

    internal ActiveFeishuApprovalCoordinator(
        IBridgeActiveApprovalStateOwner stateOwner,
        IBridgeRuntimeCommandGateway runtimeCommands,
        FeishuInteractionCoordinator interactions,
        IFeishuCardRenderer renderer)
        : this(stateOwner, () => runtimeCommands, interactions, renderer)
    {
        ArgumentNullException.ThrowIfNull(runtimeCommands);
    }

    public ActiveFeishuApprovalCoordinator(
        IBridgeActiveApprovalStateOwner stateOwner,
        Func<IBridgeRuntimeCommandGateway> runtimeCommands,
        FeishuInteractionCoordinator interactions,
        IFeishuCardRenderer renderer)
    {
        this.stateOwner = stateOwner ?? throw new ArgumentNullException(nameof(stateOwner));
        this.runtimeCommands = runtimeCommands ??
            throw new ArgumentNullException(nameof(runtimeCommands));
        this.interactions = interactions ?? throw new ArgumentNullException(nameof(interactions));
        this.renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    }

    public async Task<BridgeLocalApprovalResolveResult> HandleLocalAsync(
        string requestId,
        string resolution,
        BridgeStoreSnapshot store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        cancellationToken.ThrowIfCancellationRequested();
        requestId = requestId?.Trim() ?? string.Empty;
        if (requestId.Length is 0 or > 128 ||
            resolution is not ApprovalResolutions.Allow and not ApprovalResolutions.Deny)
        {
            return new(
                false,
                false,
                string.Empty,
                string.Empty,
                "审批请求或处理方式不正确。");
        }

        var current = stateOwner.Snapshot;
        if (!current.Initialized)
        {
            throw new InvalidOperationException("Active Host 业务状态尚未初始化。");
        }
        if (!current.Approvals.Requests.TryGetValue(requestId, out var approval) ||
            approval.Status != ApprovalStatuses.Pending)
        {
            return AlreadyResolved(approval);
        }

        var operationId = Guid.NewGuid().ToString("N");
        var result = await HandleAsync(
            new FeishuIntent(
                $"desktop-approval-{operationId}",
                FeishuIntentTypes.ApprovalResolve,
                "desktop-local",
                "desktop-local",
                "desktop-local",
                "desktop",
                $"desktop-approval-{operationId}",
                Parameters: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["requestId"] = requestId,
                    ["sessionId"] = approval.SessionId,
                    ["resolution"] = resolution,
                }),
            store,
            cancellationToken);
        if (string.Equals(result.ToastType, "success", StringComparison.Ordinal))
        {
            return new(true, false, resolution, result.ToastContent);
        }

        var observed = stateOwner.Snapshot.Approvals.Requests
            .GetValueOrDefault(requestId);
        return observed is null || observed.Status != ApprovalStatuses.Pending
            ? AlreadyResolved(observed)
            : new(
                false,
                false,
                string.Empty,
                string.Empty,
                string.IsNullOrWhiteSpace(result.ToastContent)
                    ? "审批状态没有改变，请刷新后重试。"
                    : result.ToastContent);
    }

    public async Task<FeishuCallbackResult> HandleAsync(
        FeishuIntent intent,
        BridgeStoreSnapshot store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(store);
        cancellationToken.ThrowIfCancellationRequested();

        var requestId = Parameter(intent, "requestId");
        var sessionId = Parameter(intent, "sessionId");
        if (requestId is null || sessionId is null)
        {
            return new("error", "审批参数不完整。");
        }

        var current = stateOwner.Snapshot;
        if (!current.Initialized)
        {
            throw new InvalidOperationException("Active Host 业务状态尚未初始化。");
        }
        if (!current.Approvals.Requests.TryGetValue(requestId, out var approval))
        {
            return new("error", "审批请求不存在或已失效。");
        }
        if (!string.Equals(approval.SessionId, sessionId, StringComparison.Ordinal))
        {
            return new("error", "审批请求与目标会话不匹配。");
        }
        if (!TrySession(current, store, sessionId, out var session, out var storedSession))
        {
            return new("error", "审批对应的会话不可用。");
        }
        var sessionView = SessionView(session, storedSession);
        var approvalView = ApprovalView(approval, store);

        if (approval.Status != ApprovalStatuses.Pending)
        {
            var terminalCard = TerminalCard(approval, sessionView, approvalView);
            var legacyCard = IsCardAction(intent) &&
                terminalCard is not null &&
                !approval.MessageIds.Contains(intent.MessageId, StringComparer.Ordinal);
            return await SynchronizeForCallbackAsync(
                intent,
                new(
                    "warning",
                    "这条审批已经处理或失效。",
                    legacyCard ? null : terminalCard),
                async synchronizationCancellationToken =>
                {
                    await SynchronizeTerminalAsync(
                        approval,
                        sessionView,
                        approvalView,
                        synchronizationCancellationToken);
                    if (legacyCard)
                    {
                        await Task.Delay(
                            LegacyCardPatchDelay,
                            synchronizationCancellationToken);
                        await interactions.SynchronizeApprovalMessageAsync(
                            approval,
                            sessionView,
                            approvalView,
                            intent.MessageId,
                            synchronizationCancellationToken);
                    }
                },
                cancellationToken);
        }

        var deferToLocal = string.Equals(
            intent.IntentType,
            FeishuIntentTypes.ApprovalDeferToLocal,
            StringComparison.Ordinal);
        string? resolution = null;
        if (!deferToLocal)
        {
            resolution = Parameter(intent, "resolution");
            if (resolution is not ApprovalResolutions.Allow and not ApprovalResolutions.Deny)
            {
                return new("error", "审批决定不正确。");
            }
            if (!RuntimeNames.All.Contains(session.Runtime))
            {
                return new("error", "审批对应的运行时不可用。");
            }
            try
            {
                if (!runtimeCommands().IsReady(
                        session.Runtime,
                        new RuntimeSession(session.SessionId, session.Cwd)))
                {
                    return new("warning", "对应窗口尚未就绪，未处理这条审批。");
                }
            }
            catch
            {
                return new("warning", "对应窗口尚未就绪，未处理这条审批。");
            }
        }

        var claim = await stateOwner.TryClaimApprovalAsync(
            requestId,
            sessionId,
            cancellationToken);
        if (claim is null)
        {
            var observed = stateOwner.Snapshot.Approvals.Requests
                .GetValueOrDefault(requestId);
            if (observed is not null)
            {
                return await SynchronizeForCallbackAsync(
                    intent,
                    new("warning", "这条审批已经处理或正在处理中。"),
                    synchronizationCancellationToken => SynchronizeTerminalAsync(
                        observed,
                        sessionView,
                        approvalView,
                        synchronizationCancellationToken),
                    cancellationToken);
            }
            return new("warning", "这条审批已经处理或正在处理中。");
        }

        try
        {
            if (deferToLocal)
            {
                var deferred = await stateOwner.DeferClaimedApprovalAsync(
                    requestId,
                    sessionId,
                    cancellationToken);
                if (deferred is null)
                {
                    var observed = stateOwner.Snapshot.Approvals.Requests
                        .GetValueOrDefault(requestId);
                    if (observed is not null)
                    {
                        return await SynchronizeForCallbackAsync(
                            intent,
                            new("warning", "这条审批已经处理或失效。"),
                            synchronizationCancellationToken =>
                                SynchronizeTerminalAsync(
                                    observed,
                                    sessionView,
                                    approvalView,
                                    synchronizationCancellationToken),
                            cancellationToken);
                    }
                    return new("warning", "这条审批已经处理或失效。");
                }
                return await SynchronizeForCallbackAsync(
                    intent,
                    new(
                        "success",
                        "已转回 PC 审批，请在电脑端审批窗口处理。",
                        renderer.DeferredApproval(sessionView, approvalView)),
                    synchronizationCancellationToken =>
                        interactions.SynchronizeDeferredApprovalAsync(
                            deferred.Approval,
                            sessionView,
                            approvalView,
                            synchronizationCancellationToken),
                    cancellationToken);
            }

            try
            {
                await runtimeCommands().DispatchAsync(
                    Command(intent, claim, resolution!),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new("warning", "暂时无法提交审批决定，请稍后重试。");
            }

            var resolved = await stateOwner.ResolveClaimedApprovalAsync(
                requestId,
                sessionId,
                resolution!,
                cancellationToken);
            if (resolved is null)
            {
                var observed = stateOwner.Snapshot.Approvals.Requests
                    .GetValueOrDefault(requestId);
                if (observed is not null)
                {
                    return await SynchronizeForCallbackAsync(
                        intent,
                        new("warning", "这条审批已经处理或失效。"),
                        synchronizationCancellationToken => SynchronizeTerminalAsync(
                            observed,
                            sessionView,
                            approvalView,
                            synchronizationCancellationToken),
                        cancellationToken);
                }
                return new("warning", "这条审批已经处理或失效。");
            }
            return await SynchronizeForCallbackAsync(
                intent,
                new(
                    "success",
                    resolution == ApprovalResolutions.Allow
                        ? $"已批准，{RuntimeDisplayName(session.Runtime)} 将继续执行。"
                        : "已拒绝这次操作。",
                    renderer.ResolvedApproval(
                        sessionView,
                        approvalView,
                        resolved.Approval.Resolution!,
                        resolved.Approval.Status)),
                synchronizationCancellationToken =>
                    interactions.SynchronizeApprovalAsync(
                        resolved.Approval,
                        sessionView,
                        approvalView,
                        synchronizationCancellationToken),
                cancellationToken);
        }
        finally
        {
            await stateOwner.ReleaseApprovalClaimAsync(
                requestId,
                CancellationToken.None);
        }
    }

    public async Task<FeishuCallbackResult?> TryHandleQuotedReplyAsync(
        FeishuIntent intent,
        BridgeStoreSnapshot store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(store);
        cancellationToken.ThrowIfCancellationRequested();
        var quoted = ActiveFeishuQuotedRouteLookup.Find(intent, store.Routes);
        if (quoted is null ||
            !string.Equals(quoted.Route.Kind, "approval", StringComparison.Ordinal))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(quoted.Route.RequestId))
        {
            return new("warning", "这张审批卡缺少请求信息，已无法处理。");
        }

        var action = ApprovalActionFromText(intent.Text);
        if (action is null)
        {
            return new(
                "info",
                "这个会话正在等待审批。请点击审批卡片按钮，或引用卡片回复“批准”“拒绝”或“本机确认”。");
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["requestId"] = quoted.Route.RequestId,
            ["sessionId"] = quoted.Route.SessionId,
        };
        var intentType = action == "desktop"
            ? FeishuIntentTypes.ApprovalDeferToLocal
            : FeishuIntentTypes.ApprovalResolve;
        if (action is ApprovalResolutions.Allow or ApprovalResolutions.Deny)
        {
            parameters["resolution"] = action;
        }
        var result = await HandleAsync(
            intent with
            {
                IntentType = intentType,
                Parameters = parameters,
            },
            store,
            cancellationToken);
        result = result with { Card = null };
        return action == "desktop" &&
            result.ToastType == "success"
            ? result with
            {
                ToastContent =
                    "已转回 PC 审批，电脑端审批窗口将在下一次状态刷新时弹出。",
            }
            : result;
    }

    private FeishuCardView? TerminalCard(
        ApprovalState approval,
        FeishuSessionView session,
        FeishuApprovalView view) =>
        approval.Status is ApprovalStatuses.Resolved or ApprovalStatuses.Orphaned &&
        approval.Resolution is not null
            ? renderer.ResolvedApproval(
                session,
                view,
                approval.Resolution,
                approval.Status)
            : null;

    private static RuntimeCommandEnvelope Command(
        FeishuIntent intent,
        BridgeApprovalClaim claim,
        string resolution) => new()
        {
            ProtocolVersion = BridgeProtocolVersion.Current,
            Runtime = claim.Session.Runtime,
            Session = new RuntimeSessionReference
            {
                ExternalId = claim.Session.SessionId,
                Cwd = claim.Session.Cwd,
            },
            TraceId = intent.TraceId,
            CorrelationId = intent.EventId,
            CommandId = $"feishu-approval-{claim.Approval.RequestId}",
            CommandType = RuntimeCommandTypes.ApprovalResolve,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            Payload = JsonSerializer.SerializeToElement(new
            {
                requestId = claim.Approval.RequestId,
                decision = resolution == ApprovalResolutions.Allow
                    ? "allow_once"
                    : "deny",
            }),
        };

    private Task SynchronizeTerminalAsync(
        ApprovalState approval,
        FeishuSessionView session,
        FeishuApprovalView view,
        CancellationToken cancellationToken) =>
        approval.Status is ApprovalStatuses.Resolved or ApprovalStatuses.Orphaned &&
            approval.Resolution is not null
            ? interactions.SynchronizeApprovalAsync(
                approval,
                session,
                view,
                cancellationToken)
            : Task.CompletedTask;

    private static async Task<FeishuCallbackResult> SynchronizeForCallbackAsync(
        FeishuIntent intent,
        FeishuCallbackResult result,
        Func<CancellationToken, Task> synchronize,
        CancellationToken cancellationToken)
    {
        if (!IsCardAction(intent))
        {
            await synchronize(cancellationToken);
            return result;
        }

        var existing = result.AfterAcknowledged;
        return result with
        {
            Card = null,
            AfterAcknowledged = async acknowledgedCancellationToken =>
            {
                if (existing is not null)
                {
                    await existing(acknowledgedCancellationToken);
                }
                await synchronize(acknowledgedCancellationToken);
            },
        };
    }

    private static bool TrySession(
        BridgeBusinessStateSnapshot current,
        BridgeStoreSnapshot store,
        string sessionId,
        out SessionState session,
        out SessionStoreRecord stored)
    {
        if (current.Sessions.Sessions.TryGetValue(sessionId, out session!) &&
            store.Sessions.Sessions.TryGetValue(sessionId, out stored!) &&
            string.Equals(session.Runtime, Runtime(stored), StringComparison.Ordinal) &&
            string.Equals(session.Cwd, stored.Cwd, StringComparison.Ordinal))
        {
            return true;
        }
        session = null!;
        stored = null!;
        return false;
    }

    private static FeishuSessionView SessionView(
        SessionState session,
        SessionStoreRecord stored) => new(
            session.SessionId,
            session.Runtime,
            ExtensionString(stored.ExtensionData, "alias") ??
                stored.ProjectName ??
                stored.ShortId ??
                ShortId(stored.SessionId),
            session.Cwd,
            ExtensionBoolean(stored.ExtensionData, "managedByAssistant"));

    private static FeishuApprovalView ApprovalView(
        ApprovalState approval,
        BridgeStoreSnapshot store)
    {
        var stored = store.Approvals.Requests.GetValueOrDefault(approval.RequestId);
        return new(
            approval.RequestId,
            approval.ToolName,
            approval.ToolPreview,
            ExtensionString(stored?.ExtensionData, "riskLevel") ?? "normal",
            ExtensionString(stored?.ExtensionData, "riskReason"));
    }

    private static string? Parameter(FeishuIntent intent, string name) =>
        intent.Parameters?.TryGetValue(name, out var value) == true &&
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        !value.Any(char.IsControl)
            ? value.Trim()
            : null;

    private static bool IsCardAction(FeishuIntent intent) =>
        string.Equals(intent.ChatType, "card", StringComparison.Ordinal);

    private static string Runtime(SessionStoreRecord session) =>
        string.IsNullOrWhiteSpace(session.Runtime)
            ? RuntimeNames.Codex
            : session.Runtime;

    private static string? ExtensionString(
        Dictionary<string, JsonElement>? extensions,
        string name) =>
        extensions is not null &&
        extensions.TryGetValue(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static bool ExtensionBoolean(
        Dictionary<string, JsonElement>? extensions,
        string name) =>
        extensions is not null &&
        extensions.TryGetValue(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static string ShortId(string sessionId)
    {
        var compact = new string(sessionId.Where(char.IsLetterOrDigit).ToArray());
        var source = compact.Length == 0 ? sessionId : compact;
        return source[^Math.Min(8, source.Length)..].ToLowerInvariant();
    }

    private static string RuntimeDisplayName(string runtime) => runtime switch
    {
        RuntimeNames.ClaudeCode => "Claude Code",
        RuntimeNames.OpenCode => "OpenCode",
        _ => "Codex",
    };

    private static string? ApprovalActionFromText(string? text)
    {
        var normalized = new string((text ?? string.Empty)
            .Where(character => !char.IsWhiteSpace(character) &&
                character is not '，' and not '。' and not '！' and not '!')
            .ToArray())
            .ToLowerInvariant();
        return normalized switch
        {
            "批准" or "允许" or "同意" or "approve" or "allow" =>
                ApprovalResolutions.Allow,
            "拒绝" or "不允许" or "deny" or "reject" =>
                ApprovalResolutions.Deny,
            "本机确认" or "本机审批" or "电脑确认" or "电脑审批" or "pc审批" =>
                "desktop",
            _ => null,
        };
    }

    private static BridgeLocalApprovalResolveResult AlreadyResolved(
        ApprovalState? approval) => new(
            true,
            true,
            approval?.Resolution ?? ApprovalResolutions.Local,
            "这条审批已经处理或失效。");
}
