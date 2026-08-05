using System.Text.Json;
using AiCliFeishu.Bridge.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishu.Bridge.Core.Tests;

[TestClass]
public sealed class StateReplayTests
{
    [TestMethod]
    public void SharedStateTransitionsReplayWithoutDifferences()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "MigrationExamples",
            "state-transitions.jsonl");
        var cases = File.ReadLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line))
            .ToArray();

        Assert.AreEqual(3, cases.Length);
        foreach (var document in cases)
        {
            using (document)
            {
                var root = document.RootElement;
                var caseId = root.GetProperty("caseId").GetString();
                switch (caseId)
                {
                    case "session-lifecycle":
                        ReplaySession(root);
                        break;
                    case "approval-first-writer-wins":
                        ReplayApproval(root);
                        break;
                    case "message-route-retention":
                        ReplayRoutes(root);
                        break;
                    default:
                        Assert.Fail($"未知状态回放样例 {caseId}。 ");
                        break;
                }
            }
        }
    }

    private static void ReplaySession(JsonElement root)
    {
        var initial = root.GetProperty("initial");
        var session = new SessionState(
            Text(initial, "sessionId"),
            Text(initial, "runtime"),
            Text(initial, "cwd"),
            Text(initial, "status"),
            Time(initial, "openedAt"),
            Time(initial, "lastSeenAt"));
        var state = SessionStateMachine.Register(SessionDirectoryState.Empty, session);
        foreach (var item in root.GetProperty("events").EnumerateArray())
        {
            state = SessionStateMachine.Transition(
                state,
                session.SessionId,
                Text(item, "status"),
                Time(item, "at"));
        }
        var actual = state.Sessions[session.SessionId];
        var expected = root.GetProperty("expected");
        Assert.AreEqual(Text(expected, "status"), actual.Status);
        Assert.AreEqual(Time(expected, "openedAt"), actual.OpenedAt);
        Assert.AreEqual(Time(expected, "lastSeenAt"), actual.LastSeenAt);
        Assert.IsNull(actual.EndedAt);
    }

    private static void ReplayApproval(JsonElement root)
    {
        var initial = root.GetProperty("initial");
        var state = ApprovalStateMachine.Create(
            ApprovalRegistryState.Empty,
            new ApprovalState(
                Text(initial, "requestId"),
                Text(initial, "sessionId"),
                ApprovalStatuses.Pending,
                Time(initial, "createdAt"),
                Time(initial, "expiresAt"),
                []));
        var accepted = new List<bool>();
        foreach (var item in root.GetProperty("events").EnumerateArray())
        {
            switch (Text(item, "type"))
            {
                case "approval.claim":
                    var claim = ApprovalStateMachine.Claim(state, Text(initial, "requestId"));
                    state = claim.State;
                    accepted.Add(claim.Value);
                    break;
                case "approval.resolve_claimed":
                    var local = ApprovalStateMachine.ResolveClaimed(
                        state,
                        Text(initial, "requestId"),
                        Text(item, "resolution"),
                        Time(item, "at"));
                    state = local.State;
                    accepted.Add(local.Value);
                    break;
                case "approval.resolve_external":
                    var external = ApprovalStateMachine.ResolveExternally(
                        state,
                        Text(initial, "requestId"),
                        Text(item, "resolution"),
                        Time(item, "at"));
                    state = external.State;
                    accepted.Add(external.Value);
                    break;
            }
        }
        var actual = state.Requests[Text(initial, "requestId")];
        var expected = root.GetProperty("expected");
        Assert.AreEqual(Text(expected, "status"), actual.Status);
        Assert.AreEqual(Text(expected, "resolution"), actual.Resolution);
        CollectionAssert.AreEqual(
            expected.GetProperty("accepted").EnumerateArray().Select(item => item.GetBoolean()).ToArray(),
            accepted.ToArray());
    }

    private static void ReplayRoutes(JsonElement root)
    {
        var initial = root.GetProperty("initial");
        var policy = RetentionPolicy.Default with
        {
            RouteRetention = TimeSpan.FromSeconds(
                initial.GetProperty("routeRetentionSeconds").GetInt32()),
            MaxMessageRoutes = initial.GetProperty("maxMessageRoutes").GetInt32(),
        };
        var state = MessageRouteRegistryState.Empty;
        foreach (var item in root.GetProperty("events").EnumerateArray())
        {
            if (Text(item, "type") == "route.add")
            {
                var messageId = Text(item, "messageId");
                state = MessageRouteStateMachine.AddRoute(
                    state,
                    new MessageRouteState(
                        messageId, "session-1", "chat-1", "activity", Time(item, "at")));
            }
            else
            {
                state = MessageRouteStateMachine.Prune(state, Time(item, "at"), policy);
            }
        }
        CollectionAssert.AreEqual(
            root.GetProperty("expected").GetProperty("messageIds")
                .EnumerateArray().Select(item => item.GetString()).ToArray(),
            state.Messages.Keys.Order(StringComparer.Ordinal).Cast<string?>().ToArray());
    }

    private static string Text(JsonElement owner, string property) =>
        owner.GetProperty(property).GetString()
        ?? throw new InvalidDataException($"{property} 不能为空。 ");

    private static DateTimeOffset Time(JsonElement owner, string property) =>
        DateTimeOffset.Parse(Text(owner, property));
}
