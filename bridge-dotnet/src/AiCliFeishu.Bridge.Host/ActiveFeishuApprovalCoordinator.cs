using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed class ActiveFeishuApprovalCoordinator(
    IBridgeActiveApprovalStateOwner stateOwner,
    IBridgeRuntimeCommandGateway runtimeCommands,
    FeishuInteractionCoordinator interactions)
{
    public async Task<FeishuCallbackResult> HandleAsync(
        FeishuIntent intent,
        NodeStoreSnapshot store,
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
            await SynchronizeTerminalAsync(
                approval,
                sessionView,
                approvalView,
                cancellationToken);
            return new("warning", "这条审批已经处理或失效。");
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
                if (!runtimeCommands.IsReady(
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
                await SynchronizeTerminalAsync(
                    observed,
                    sessionView,
                    approvalView,
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
                        await SynchronizeTerminalAsync(
                            observed,
                            sessionView,
                            approvalView,
                            cancellationToken);
                    }
                    return new("warning", "这条审批已经处理或失效。");
                }
                await interactions.SynchronizeDeferredApprovalAsync(
                    deferred.Approval,
                    sessionView,
                    approvalView,
                    cancellationToken);
                return new(
                    "success",
                    "已转回 PC 审批，请在电脑端审批窗口处理。");
            }

            try
            {
                await runtimeCommands.DispatchAsync(
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
                    await SynchronizeTerminalAsync(
                        observed,
                        sessionView,
                        approvalView,
                        cancellationToken);
                }
                return new("warning", "这条审批已经处理或失效。");
            }
            await interactions.SynchronizeApprovalAsync(
                resolved.Approval,
                sessionView,
                approvalView,
                cancellationToken);
            return new(
                "success",
                resolution == ApprovalResolutions.Allow
                    ? $"已批准，{RuntimeDisplayName(session.Runtime)} 将继续执行。"
                    : "已拒绝这次操作。");
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
        NodeStoreSnapshot store,
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
        return action == "desktop" &&
            result.ToastType == "success"
            ? result with
            {
                ToastContent =
                    "已转回 PC 审批，电脑端审批窗口将在下一次状态刷新时弹出。",
            }
            : result;
    }

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

    private static bool TrySession(
        BridgeBusinessStateSnapshot current,
        NodeStoreSnapshot store,
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
        NodeStoreSnapshot store)
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
}
