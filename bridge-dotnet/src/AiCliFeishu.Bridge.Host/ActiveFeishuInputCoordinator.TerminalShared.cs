using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed partial class ActiveFeishuInputCoordinator
{
    private bool RuntimeReady(SessionState session)
    {
        if (!RuntimeNames.All.Contains(session.Runtime))
        {
            return false;
        }
        try
        {
            return runtimeCommands.IsReady(
                session.Runtime,
                new RuntimeSession(session.SessionId, session.Cwd));
        }
        catch
        {
            return false;
        }
    }

    private static RuntimeCommandEnvelope Command(
        FeishuIntent intent,
        BridgeInputAnswerProgress progress) => new()
        {
            ProtocolVersion = BridgeProtocolVersion.Current,
            Runtime = progress.Session.Runtime,
            Session = new RuntimeSessionReference
            {
                ExternalId = progress.Session.SessionId,
                Cwd = progress.Session.Cwd,
            },
            TraceId = intent.TraceId,
            CorrelationId = intent.EventId,
            CommandId = $"feishu-input-{progress.Input.RequestId}",
            CommandType = RuntimeCommandTypes.InputResolve,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            Payload = JsonSerializer.SerializeToElement(new
            {
                requestId = progress.Input.RequestId,
                answers = progress.Input.Answers.ToDictionary(
                    item => item.Key,
                    item => item.Value.ToArray(),
                    StringComparer.Ordinal),
            }),
        };

    private InputContext? TryContext(
        string requestId,
        string sessionId,
        BridgeStoreSnapshot store)
    {
        var current = stateOwner.Snapshot;
        if (!current.Initialized)
        {
            throw new InvalidOperationException("Active Host 业务状态尚未初始化。");
        }
        if (!current.Inputs.Requests.TryGetValue(requestId, out var input) ||
            !string.Equals(input.SessionId, sessionId, StringComparison.Ordinal) ||
            !current.Sessions.Sessions.TryGetValue(sessionId, out var session) ||
            !store.Sessions.Sessions.TryGetValue(sessionId, out var stored) ||
            !string.Equals(session.Runtime, Runtime(stored), StringComparison.Ordinal) ||
            !string.Equals(session.Cwd, stored.Cwd, StringComparison.Ordinal))
        {
            return null;
        }
        return Context(input, session, stored);
    }

    private static InputContext Context(
        InputRequestState input,
        SessionState session,
        SessionStoreRecord stored)
    {
        var questions = input.Questions.Select((question, index) => new FeishuInputQuestionView(
            question.Id,
            string.IsNullOrWhiteSpace(question.Header)
                ? $"问题 {index + 1}"
                : question.Header,
            string.IsNullOrWhiteSpace(question.Prompt)
                ? question.Id
                : question.Prompt,
            question.Multiple,
            question.AllowsCustom,
            question.IsSecret,
            question.Options)).ToArray();
        return new(
            input,
            session,
            stored,
            questions,
            new(
                session.SessionId,
                session.Runtime,
                ExtensionString(stored.ExtensionData, "alias") ??
                    stored.ProjectName ??
                    stored.ShortId ??
                    ShortId(stored.SessionId),
                session.Cwd));
    }

    private static InputQuestionState? Question(InputContext context, string? questionId) =>
        questionId is null
            ? null
            : context.Input.Questions.SingleOrDefault(item => string.Equals(
                item.Id,
                questionId,
                StringComparison.Ordinal));

    private async Task RespondAsync(
        FeishuIntent intent,
        string text,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await gateway.ReplyTextAsync(intent.MessageId, text, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _ = await gateway.SendTextAsync(intent.ChatId, text, cancellationToken);
        }
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

    private static string? ExtensionString(
        Dictionary<string, JsonElement>? extensions,
        string name) =>
        extensions is not null &&
        extensions.TryGetValue(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static string Runtime(SessionStoreRecord session) =>
        string.IsNullOrWhiteSpace(session.Runtime)
            ? RuntimeNames.Codex
            : session.Runtime;

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

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : $"{value[..Math.Max(0, length - 1)]}…";

    private sealed record InputContext(
        InputRequestState Input,
        SessionState Session,
        SessionStoreRecord StoredSession,
        IReadOnlyList<FeishuInputQuestionView> Questions,
        FeishuSessionView SessionView);

    private readonly record struct InputSelectionKey(
        string RequestId,
        string QuestionId,
        string SelectionKey);
}
