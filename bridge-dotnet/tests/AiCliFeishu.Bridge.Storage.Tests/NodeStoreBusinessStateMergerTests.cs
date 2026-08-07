using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishu.Bridge.Storage.Tests;

[TestClass]
public sealed class NodeStoreBusinessStateMergerTests
{
    private static readonly DateTimeOffset Origin =
        DateTimeOffset.Parse("2026-08-06T00:00:00Z");

    [TestMethod]
    public void MergesBusinessStateWithoutLosingNodeFieldsOrUnrelatedDocuments()
    {
        var source = Store();
        var sessions = SessionStateMachine.Transition(
            NodeStoreCoreProjection.Project(source).Sessions,
            "session-1",
            SessionStatuses.Running,
            Origin.AddMinutes(2));
        var approvals = ApprovalStateMachine.ResolveExternally(
            NodeStoreCoreProjection.Project(source).Approvals,
            "approval-1",
            ApprovalResolutions.Allow,
            Origin.AddMinutes(3)).State;

        var merged = NodeStoreBusinessStateMerger.Merge(source, sessions, approvals);

        var session = merged.Sessions.Sessions["session-1"];
        Assert.AreEqual("existing-short", session.ShortId);
        Assert.AreEqual("existing-project", session.ProjectName);
        Assert.AreEqual(SessionStatuses.Running, session.Status);
        Assert.AreEqual("keep-session", session.ExtensionData!["futureSession"].GetString());
        var approval = merged.Approvals.Requests["approval-1"];
        Assert.AreEqual("turn-1", approval.TurnId);
        Assert.AreEqual("shell_command", approval.ToolName);
        Assert.AreEqual("keep-approval", approval.ExtensionData!["futureApproval"].GetString());
        Assert.AreEqual(ApprovalStatuses.Resolved, approval.Status);
        Assert.AreSame(source.Bindings, merged.Bindings);
        Assert.AreSame(source.Routes, merged.Routes);
        Assert.AreSame(source.Settings, merged.Settings);
        Assert.AreSame(source.ControlToken, merged.ControlToken);
    }

    [TestMethod]
    public void CreatesNodeCompatibleRecordsForNewBusinessState()
    {
        var session = new SessionState(
            "new-session/ABC-12345678",
            "codex",
            "K:/repo/new-project",
            SessionStatuses.PendingApproval,
            Origin,
            Origin);
        var sessions = SessionStateMachine.Register(SessionDirectoryState.Empty, session);
        var approval = new ApprovalState(
            "approval-new",
            session.SessionId,
            ApprovalStatuses.Pending,
            Origin,
            Origin.AddMinutes(20),
            [],
            TurnId: "turn-new",
            Cwd: session.Cwd,
            ToolName: "shell_command",
            ToolPreview: "echo test");
        var approvals = ApprovalStateMachine.Create(ApprovalRegistryState.Empty, approval);

        var merged = NodeStoreBusinessStateMerger.Merge(EmptyStore(), sessions, approvals);
        var json = NodeStoreJson.Serialize(merged.Sessions);
        using var sessionsJson = JsonDocument.Parse(json);
        NodeStoreValidator.Validate(NodeStoreFile.Sessions, sessionsJson.RootElement);
        json = NodeStoreJson.Serialize(merged.Approvals);
        using var approvalsJson = JsonDocument.Parse(json);
        NodeStoreValidator.Validate(NodeStoreFile.Approvals, approvalsJson.RootElement);

        Assert.AreEqual("12345678", merged.Sessions.Sessions[session.SessionId].ShortId);
        Assert.AreEqual("new-project", merged.Sessions.Sessions[session.SessionId].ProjectName);
        Assert.AreEqual("turn-new", merged.Approvals.Requests[approval.RequestId].TurnId);
    }

    private static NodeStoreSnapshot Store()
    {
        var store = EmptyStore();
        store.Sessions.ExtensionData = new()
        {
            ["futureRoot"] = JsonSerializer.SerializeToElement("keep-root"),
        };
        store.Sessions.Sessions["session-1"] = new()
        {
            SessionId = "session-1",
            ShortId = "existing-short",
            Cwd = "K:/repo",
            ProjectName = "existing-project",
            Runtime = "codex",
            Status = SessionStatuses.Waiting,
            OpenedAt = Origin.ToString("O"),
            LastSeenAt = Origin.AddMinutes(1).ToString("O"),
            ExtensionData = new()
            {
                ["futureSession"] = JsonSerializer.SerializeToElement("keep-session"),
            },
        };
        store.Approvals.Requests["approval-1"] = new()
        {
            RequestId = "approval-1",
            SessionId = "session-1",
            TurnId = "turn-1",
            Cwd = "K:/repo",
            ToolName = "shell_command",
            ToolPreview = "echo test",
            CreatedAt = Origin.ToString("O"),
            ExpiresAt = Origin.AddMinutes(20).ToString("O"),
            Status = ApprovalStatuses.Pending,
            ExtensionData = new()
            {
                ["futureApproval"] = JsonSerializer.SerializeToElement("keep-approval"),
            },
        };
        return store;
    }

    private static NodeStoreSnapshot EmptyStore() => new(
        new BindingStoreDocument(),
        new SessionStoreDocument(),
        new RouteStoreDocument(),
        new ApprovalStoreDocument(),
        new SettingsStoreDocument(),
        new ControlTokenStoreDocument());
}
