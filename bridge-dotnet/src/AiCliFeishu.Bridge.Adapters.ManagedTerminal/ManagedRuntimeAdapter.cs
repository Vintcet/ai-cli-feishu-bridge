using System.Text.Json;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Adapters.ManagedTerminal;

public abstract class ManagedRuntimeAdapter : IRuntimeAdapter
{
    private static readonly IReadOnlySet<RuntimeCapability> RuntimeCapabilities =
        new HashSet<RuntimeCapability>
        {
            RuntimeCapability.PromptSend,
            RuntimeCapability.PromptQueue,
            RuntimeCapability.ApprovalResolve,
            RuntimeCapability.InputResolve,
            RuntimeCapability.SessionLaunch,
            RuntimeCapability.SessionResume,
            RuntimeCapability.SessionStop,
            RuntimeCapability.ActivityStream,
        };

    private readonly IManagedTerminalDirectory terminals;
    private readonly IManagedTerminalTransport terminalTransport;
    private readonly IManagedRuntimeLifecycle lifecycle;
    private readonly IManagedHookResponseSink hookResponses;

    protected ManagedRuntimeAdapter(
        string runtime,
        IManagedTerminalDirectory terminals,
        IManagedTerminalTransport terminalTransport,
        IManagedRuntimeLifecycle lifecycle,
        IManagedHookResponseSink hookResponses)
    {
        if (runtime is not RuntimeNames.Codex and not RuntimeNames.ClaudeCode)
        {
            throw new ArgumentException("托管终端 Adapter 仅支持 Codex 和 Claude Code。", nameof(runtime));
        }
        Runtime = runtime;
        this.terminals = terminals;
        this.terminalTransport = terminalTransport;
        this.lifecycle = lifecycle;
        this.hookResponses = hookResponses;
    }

    public string Runtime { get; }

    public IReadOnlySet<RuntimeCapability> Capabilities => RuntimeCapabilities;

    public bool IsReady(RuntimeSession session)
    {
        var target = terminals.FindBySession(session.ExternalId);
        return target is { Ready: true } &&
            string.Equals(target.SessionExternalId, session.ExternalId, StringComparison.Ordinal);
    }

    public async Task ExecuteAsync(
        RuntimeCommandEnvelope command,
        CancellationToken cancellationToken = default)
    {
        ValidateCommandTarget(command);
        var session = command.Session!;
        var context = RuntimeCommandContext.From(command);
        switch (command.CommandType)
        {
            case RuntimeCommandTypes.PromptSend:
                await SendPromptAsync(context, command, cancellationToken);
                break;
            case RuntimeCommandTypes.ApprovalResolve:
                await hookResponses.ResolveApprovalAsync(
                    context,
                    Runtime,
                    session.ExternalId,
                    RequiredString(command.Payload, "requestId"),
                    RequiredString(command.Payload, "decision"),
                    cancellationToken);
                break;
            case RuntimeCommandTypes.InputResolve:
                await hookResponses.ResolveInputAsync(
                    context,
                    Runtime,
                    session.ExternalId,
                    RequiredString(command.Payload, "requestId"),
                    ReadAnswers(command.Payload),
                    cancellationToken);
                break;
            case RuntimeCommandTypes.SessionLaunch:
                await lifecycle.LaunchAsync(
                    context,
                    Runtime,
                    session.ExternalId,
                    RequiredString(command.Payload, "cwd"),
                    OptionalString(command.Payload, "prompt"),
                    OptionalBoolean(command.Payload, "elevated"),
                    cancellationToken);
                break;
            case RuntimeCommandTypes.SessionResume:
                await lifecycle.ResumeAsync(
                    context,
                    Runtime,
                    session.ExternalId,
                    session.Cwd,
                    OptionalString(command.Payload, "prompt"),
                    cancellationToken);
                break;
            case RuntimeCommandTypes.SessionStop:
                await lifecycle.StopAsync(
                    context,
                    Runtime,
                    session.ExternalId,
                    OptionalString(command.Payload, "reason"),
                    cancellationToken);
                break;
            default:
                throw new NotSupportedException($"{Runtime} 不支持命令 {command.CommandType}。");
        }
    }

    private async Task SendPromptAsync(
        RuntimeCommandContext context,
        RuntimeCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        var sessionExternalId = command.Session!.ExternalId;
        var target = terminals.FindBySession(sessionExternalId)
            ?? throw new InvalidOperationException("对应的同步窗口已经关闭或暂时离线。");
        if (!target.Ready ||
            !string.Equals(target.SessionExternalId, sessionExternalId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("托管终端与目标会话不匹配或尚未就绪。");
        }
        var mode = RequiredString(command.Payload, "mode") switch
        {
            "steer" => ManagedTerminalSubmitMode.Steer,
            "queue" => ManagedTerminalSubmitMode.Queue,
            var value => throw new InvalidDataException($"未知的提示提交模式 {value}。"),
        };
        await terminalTransport.SendAsync(
            context,
            target,
            RequiredString(command.Payload, "prompt"),
            mode,
            cancellationToken);
    }

    private void ValidateCommandTarget(RuntimeCommandEnvelope command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!string.Equals(command.Runtime, Runtime, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{Runtime} Adapter 不能执行 {command.Runtime} 命令。");
        }
        if (command.Session is null || string.IsNullOrWhiteSpace(command.Session.ExternalId))
        {
            throw new InvalidDataException("命令缺少外部会话 ID。");
        }
    }

    private static string RequiredString(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"payload.{name} 必须是非空字符串。");
        }
        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement payload, string name)
    {
        return payload.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static bool OptionalBoolean(JsonElement payload, string name)
    {
        return payload.TryGetProperty(name, out var value) &&
            value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            value.GetBoolean();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadAnswers(
        JsonElement payload)
    {
        if (!payload.TryGetProperty("answers", out var answers) ||
            answers.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("payload.answers 必须是对象。");
        }
        return answers.EnumerateObject().ToDictionary(
            property => property.Name,
            property => (IReadOnlyList<string>)(property.Value.ValueKind == JsonValueKind.Array
                ? property.Value.EnumerateArray().Select(value => value.GetString()!).ToArray()
                : [property.Value.GetString()!]),
            StringComparer.Ordinal);
    }
}
