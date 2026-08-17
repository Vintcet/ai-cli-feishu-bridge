using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed partial class ActiveFeishuIntentHandler
{
    private async Task<FeishuCallbackResult?> HandleCommandNewAsync(
        FeishuIntent intent,
        SettingsStoreDocument settings,
        CancellationToken cancellationToken)
    {
        var text = intent.Text?.Trim();
        if (IsCardAction(intent) ||
            text is null or "新建" or "/新建" ||
            string.Equals(text, "/new", StringComparison.OrdinalIgnoreCase))
        {
            return await PresentCardAsync(
                intent,
                renderer.RuntimeSelection(
                    settings.WorkspaceRoot,
                    new(
                        Guid.NewGuid().ToString("N"),
                        intent.MessageId,
                        intent.ChatId)),
                "请选择运行环境。",
                cancellationToken);
        }

        var command = FeishuNewRuntimeCommandParser.Parse(text);
        if (command is null)
        {
            return await RespondTextAsync(
                intent,
                FeishuNewRuntimeCommandParser.Usage(),
                cancellationToken);
        }
        var projectName = BridgeWorkspaceProjectDirectory.NormalizeAndValidateName(
            command.ProjectName,
            out var validationError);
        if (projectName is null)
        {
            return await RespondTextAsync(
                intent,
                $"项目名不正确：{validationError}",
                cancellationToken);
        }
        if (string.IsNullOrWhiteSpace(settings.WorkspaceRoot))
        {
            return await RespondTextAsync(
                intent,
                "尚未设置默认工作区。请先在电脑端“设置”中选择默认工作区。",
                cancellationToken);
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
            return await RespondTextAsync(
                intent,
                $"项目目录准备失败：{error.Message}",
                cancellationToken);
        }

        try
        {
            await DispatchNewRuntimeAsync(
                intent,
                command.Runtime,
                prepared,
                intent.EventId,
                notifyFailure: true,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            BridgeWorkspaceProjectDirectory.Rollback(prepared);
            throw;
        }
        catch
        {
            BridgeWorkspaceProjectDirectory.Rollback(prepared);
            return await RespondTextAsync(
                intent,
                $"{RuntimeDisplayName(command.Runtime)} 未启动：启动请求提交失败，请稍后重试。",
                cancellationToken);
        }

        return await RespondTextAsync(
            intent,
            $"{(prepared.Created ? "已创建" : "已找到")}项目“{projectName}”：" +
            $"{prepared.Cwd}\n正在请求电脑端启动 " +
            $"{RuntimeDisplayName(command.Runtime)}；会话登记后会自动创建对应飞书群。",
            cancellationToken);
    }

    private async Task<FeishuCallbackResult> StopRetryAsync(
        FeishuIntent intent,
        CancellationToken cancellationToken)
    {
        var sessionId = ShortParameter(intent.Parameters, "sessionId", 256);
        var cycleId = ShortParameter(intent.Parameters, "retryCycleId", 128);
        if (sessionId is null || cycleId is null)
        {
            return new("error", "自动重试参数不完整。");
        }

        var result = await runtimeRetries.StopAsync(
            sessionId,
            cycleId,
            intent.MessageId,
            cancellationToken);
        if (result.Kind == BridgeRetryStopKinds.Stale)
        {
            return new(
                "warning",
                "这轮自动重试已经结束，或已被新的任务替代。");
        }
        return new(
            result.Kind == BridgeRetryStopKinds.AlreadyStopped ? "info" : "success",
            result.RetryAlreadyStarted
                ? "本次重试已经发送，已停止后续自动重试。"
                : result.Kind == BridgeRetryStopKinds.AlreadyStopped
                    ? "自动重试已经停止。"
                    : "已停止自动重试。",
            result.AfterAcknowledged is null ? result.Card : null,
            result.AfterAcknowledged);
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
            await DispatchNewRuntimeAsync(
                intent,
                runtime,
                prepared,
                context.FlowId,
                notifyFailure: false,
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

    private async Task DispatchNewRuntimeAsync(
        FeishuIntent intent,
        string runtime,
        BridgePreparedProjectDirectory prepared,
        string correlationId,
        bool notifyFailure,
        CancellationToken cancellationToken)
    {
        var sessionExternalId = $"launch-{Guid.NewGuid():N}";
        if (notifyFailure)
        {
            launchNotifications.Track(
                sessionExternalId,
                runtime,
                intent.MessageId,
                intent.ChatId);
        }
        try
        {
            await runtimeCommands.DispatchAsync(
                new()
                {
                    ProtocolVersion = BridgeProtocolVersion.Current,
                    Runtime = runtime,
                    Session = new RuntimeSessionReference
                    {
                        ExternalId = sessionExternalId,
                        Cwd = prepared.Cwd,
                    },
                    TraceId = intent.TraceId,
                    CorrelationId = correlationId,
                    CommandId = $"feishu-launch-{Guid.NewGuid():N}",
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
            if (notifyFailure)
            {
                launchNotifications.Cancel(sessionExternalId);
            }
            throw;
        }
    }

}
