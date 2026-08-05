using System.Text.Json;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Adapters.OpenCode;

public sealed class OpenCodeRuntimeAdapter(IOpenCodeTransport transport) : IRuntimeAdapter
{
    private static readonly IReadOnlySet<RuntimeCapability> RuntimeCapabilities =
        new HashSet<RuntimeCapability>
        {
            RuntimeCapability.PromptSend,
            RuntimeCapability.ApprovalResolve,
            RuntimeCapability.InputResolve,
            RuntimeCapability.SessionLaunch,
            RuntimeCapability.SessionResume,
            RuntimeCapability.SessionStop,
            RuntimeCapability.ActivityStream,
        };

    public string Runtime => RuntimeNames.OpenCode;

    public IReadOnlySet<RuntimeCapability> Capabilities => RuntimeCapabilities;

    public bool IsReady(RuntimeSession session) => transport.IsReady(session.ExternalId);

    public async Task ExecuteAsync(
        RuntimeCommandEnvelope command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Runtime != RuntimeNames.OpenCode || command.Session is null)
        {
            throw new InvalidOperationException("OpenCode Adapter 收到了其他运行时的命令。");
        }
        var sessionId = command.Session.ExternalId;
        var context = RuntimeCommandContext.From(command);
        switch (command.CommandType)
        {
            case RuntimeCommandTypes.PromptSend:
                if (RequiredString(command.Payload, "mode") != "steer")
                {
                    throw new NotSupportedException("OpenCode 不支持原生消息排队。");
                }
                await transport.SendPromptAsync(
                    context,
                    sessionId,
                    RequiredString(command.Payload, "prompt"),
                    cancellationToken);
                break;
            case RuntimeCommandTypes.ApprovalResolve:
                await transport.ResolveApprovalAsync(
                    context,
                    sessionId,
                    RequiredString(command.Payload, "requestId"),
                    RequiredString(command.Payload, "decision"),
                    cancellationToken);
                break;
            case RuntimeCommandTypes.InputResolve:
                await transport.ResolveInputAsync(
                    context,
                    sessionId,
                    RequiredString(command.Payload, "requestId"),
                    ReadAnswers(command.Payload),
                    cancellationToken);
                break;
            case RuntimeCommandTypes.SessionLaunch:
                await transport.LaunchAsync(
                    context,
                    sessionId,
                    RequiredString(command.Payload, "cwd"),
                    OptionalString(command.Payload, "prompt"),
                    OptionalBoolean(command.Payload, "elevated"),
                    cancellationToken);
                break;
            case RuntimeCommandTypes.SessionResume:
                await transport.ResumeAsync(
                    context,
                    sessionId,
                    OptionalString(command.Payload, "prompt"),
                    cancellationToken);
                break;
            case RuntimeCommandTypes.SessionStop:
                await transport.StopAsync(
                    context,
                    sessionId,
                    OptionalString(command.Payload, "reason"),
                    cancellationToken);
                break;
            default:
                throw new NotSupportedException($"OpenCode 不支持命令 {command.CommandType}。");
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

    private static string? OptionalString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool OptionalBoolean(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();

    private static IReadOnlyList<IReadOnlyList<string>> ReadAnswers(
        JsonElement payload)
    {
        if (!payload.TryGetProperty("answers", out var answers) ||
            answers.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("payload.answers 必须是对象。");
        }
        const string questionPrefix = "opencode_question_";
        var orderedAnswers = answers.EnumerateObject()
            .Select(property =>
            {
                if (!property.Name.StartsWith(questionPrefix, StringComparison.Ordinal) ||
                    !int.TryParse(
                        property.Name.AsSpan(questionPrefix.Length),
                        out var questionIndex) ||
                    questionIndex < 1)
                {
                    throw new InvalidDataException(
                        $"OpenCode 问题 ID {property.Name} 无效。");
                }
                IReadOnlyList<string> values = property.Value.ValueKind == JsonValueKind.Array
                    ? property.Value.EnumerateArray().Select(value => value.GetString()!).ToArray()
                    : [property.Value.GetString()!];
                return (QuestionIndex: questionIndex, Values: values);
            })
            .OrderBy(item => item.QuestionIndex)
            .ToArray();
        for (var index = 0; index < orderedAnswers.Length; index++)
        {
            if (orderedAnswers[index].QuestionIndex != index + 1)
            {
                throw new InvalidDataException("OpenCode 问题答案必须按连续的问题 ID 提交。");
            }
        }
        return orderedAnswers.Select(item => item.Values).ToArray();
    }
}
