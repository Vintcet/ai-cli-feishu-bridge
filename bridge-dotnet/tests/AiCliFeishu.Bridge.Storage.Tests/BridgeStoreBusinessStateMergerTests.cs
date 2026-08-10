using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishu.Bridge.Storage.Tests;

[TestClass]
public sealed class BridgeStoreBusinessStateMergerTests
{
    private static readonly DateTimeOffset Origin =
        DateTimeOffset.Parse("2026-08-06T00:00:00Z");

    [TestMethod]
    public void MergesBusinessStateWithoutLosingExistingFieldsOrUnrelatedDocuments()
    {
        var source = Store();
        var sessions = SessionStateMachine.Transition(
            BridgeStoreCoreProjection.Project(source).Sessions,
            "session-1",
            SessionStatuses.Running,
            Origin.AddMinutes(2));
        var approvals = ApprovalStateMachine.ResolveExternally(
            BridgeStoreCoreProjection.Project(source).Approvals,
            "approval-1",
            ApprovalResolutions.Allow,
            Origin.AddMinutes(3)).State;

        var merged = BridgeStoreBusinessStateMerger.Merge(source, sessions, approvals);

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
    public void CreatesCompatibleRecordsForNewBusinessState()
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

        var merged = BridgeStoreBusinessStateMerger.Merge(EmptyStore(), sessions, approvals);
        var json = BridgeStoreJson.Serialize(merged.Sessions);
        using var sessionsJson = JsonDocument.Parse(json);
        BridgeStoreValidator.Validate(BridgeStoreFile.Sessions, sessionsJson.RootElement);
        json = BridgeStoreJson.Serialize(merged.Approvals);
        using var approvalsJson = JsonDocument.Parse(json);
        BridgeStoreValidator.Validate(BridgeStoreFile.Approvals, approvalsJson.RootElement);

        Assert.AreEqual("12345678", merged.Sessions.Sessions[session.SessionId].ShortId);
        Assert.AreEqual("new-project", merged.Sessions.Sessions[session.SessionId].ProjectName);
        Assert.AreEqual("turn-new", merged.Approvals.Requests[approval.RequestId].TurnId);
    }

    [TestMethod]
    public void SessionExtensionPatchReplacesKnownKeysAndPreservesUnknownFields()
    {
        var source = Store();
        source.Sessions.Sessions["session-1"].ExtensionData!["MANAGEDTERMINALID"] =
            JsonSerializer.SerializeToElement("terminal-old");
        var core = BridgeStoreCoreProjection.Project(source);
        var patch = new BridgeStoreSessionExtensionPatch(
            "session-1",
            new Dictionary<string, JsonElement>
            {
                ["managedTerminalId"] =
                    JsonSerializer.SerializeToElement("terminal-new"),
                ["managedTerminalElevated"] = JsonSerializer.SerializeToElement(true),
            });

        var merged = BridgeStoreBusinessStateMerger.Merge(
            source,
            core.Sessions,
            core.Approvals,
            patch);

        var extensions = merged.Sessions.Sessions["session-1"].ExtensionData!;
        Assert.AreEqual("terminal-new", extensions["managedTerminalId"].GetString());
        Assert.IsTrue(extensions["managedTerminalElevated"].GetBoolean());
        Assert.AreEqual("keep-session", extensions["futureSession"].GetString());
        Assert.IsFalse(extensions.ContainsKey("MANAGEDTERMINALID"));
    }

    [TestMethod]
    public void ApprovalExtensionPatchPreservesPendingStateAndUnknownFields()
    {
        var source = Store();
        source.Approvals.Requests["approval-1"].ExtensionData![
            "DESKTOPAPPROVALREQUESTED"] = JsonSerializer.SerializeToElement(false);
        var core = BridgeStoreCoreProjection.Project(source);
        var patch = new BridgeStoreApprovalExtensionPatch(
            "approval-1",
            new Dictionary<string, JsonElement>
            {
                ["desktopApprovalRequested"] = JsonSerializer.SerializeToElement(true),
            });

        var merged = BridgeStoreBusinessStateMerger.Merge(
            source,
            core.Sessions,
            core.Approvals,
            approvalExtensionPatch: patch);

        var approval = merged.Approvals.Requests["approval-1"];
        Assert.AreEqual(ApprovalStatuses.Pending, approval.Status);
        Assert.IsTrue(approval.ExtensionData!["desktopApprovalRequested"].GetBoolean());
        Assert.AreEqual(
            "keep-approval",
            approval.ExtensionData["futureApproval"].GetString());
        Assert.IsFalse(approval.ExtensionData.ContainsKey("DESKTOPAPPROVALREQUESTED"));
    }

    [TestMethod]
    public void DirectSessionExtensionPatchAddsAndRemovesFieldsWithoutStaleBusinessMerge()
    {
        var source = Store();
        source.Sessions.Sessions["session-1"].ExtensionData!["LASTNOTIFICATIONSTATUS"] =
            JsonSerializer.SerializeToElement("pending");
        var patch = new Dictionary<string, JsonElement?>
        {
            ["lastNotificationStatus"] = JsonSerializer.SerializeToElement("sent"),
            ["lastNotificationTurnId"] = JsonSerializer.SerializeToElement("turn-2"),
            ["futureSession"] = null,
        };

        var updated = BridgeStoreBusinessStateMerger.PatchSessionExtensions(
            source,
            "session-1",
            patch);

        var session = updated.Sessions.Sessions["session-1"];
        Assert.AreEqual("existing-short", session.ShortId);
        Assert.AreEqual("existing-project", session.ProjectName);
        Assert.AreEqual(SessionStatuses.Waiting, session.Status);
        Assert.AreEqual("sent", session.ExtensionData!["lastNotificationStatus"].GetString());
        Assert.AreEqual("turn-2", session.ExtensionData["lastNotificationTurnId"].GetString());
        Assert.IsFalse(session.ExtensionData.ContainsKey("LASTNOTIFICATIONSTATUS"));
        Assert.IsFalse(session.ExtensionData.ContainsKey("futureSession"));
        Assert.AreSame(source.Bindings, updated.Bindings);
        Assert.AreSame(source.Routes, updated.Routes);
        Assert.AreSame(source.Approvals, updated.Approvals);
        Assert.AreSame(source.Settings, updated.Settings);
        Assert.AreSame(source.ControlToken, updated.ControlToken);
        Assert.AreEqual(
            "keep-root",
            updated.Sessions.ExtensionData!["futureRoot"].GetString());
    }

    [TestMethod]
    public void PendingInputsRoundTripInsideSessionsExtensionAndSecretAnswersStayInMemoryOnly()
    {
        var source = Store();
        var core = BridgeStoreCoreProjection.Project(source);
        var questions = new[]
        {
            new InputQuestionState(
                "mode",
                Multiple: false,
                AllowsCustom: false,
                Options: ["safe", "fast"],
                Prompt: "请选择模式"),
            new InputQuestionState(
                "token",
                Multiple: false,
                AllowsCustom: true,
                Options: [],
                Prompt: "请输入令牌",
                IsSecret: true),
        };
        var pending = new InputRequestState(
            "input-1",
            "session-1",
            InputRequestStatuses.Pending,
            Origin.AddMinutes(2),
            Origin.AddMinutes(20),
            questions,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["mode"] = ["safe"],
                ["token"] = ["do-not-persist"],
            });
        var resolved = pending with
        {
            RequestId = "input-resolved",
            Status = InputRequestStatuses.Resolved,
            ResolvedAt = Origin.AddMinutes(3),
        };
        var inputs = new InputRegistryState(
            new Dictionary<string, InputRequestState>(StringComparer.Ordinal)
            {
                [pending.RequestId] = pending,
                [resolved.RequestId] = resolved,
            });

        var merged = BridgeStoreBusinessStateMerger.Merge(
            source,
            core.Sessions,
            core.Approvals,
            inputs);

        BridgeStoreValidator.ValidateSnapshot(merged);
        var extension = merged.Sessions.ExtensionData!["pendingInputs"];
        Assert.IsTrue(extension.TryGetProperty("input-1", out var persisted));
        Assert.IsFalse(extension.TryGetProperty("input-resolved", out _));
        Assert.IsTrue(persisted.GetProperty("answers").TryGetProperty("mode", out _));
        Assert.IsFalse(persisted.GetProperty("answers").TryGetProperty("token", out _));
        var projected = BridgeStoreCoreProjection.ProjectInputs(merged);
        Assert.AreEqual(1, projected.Requests.Count);
        Assert.AreEqual("请选择模式", projected.Requests["input-1"].Questions[0].Prompt);
        CollectionAssert.AreEqual(
            new[] { "safe" },
            projected.Requests["input-1"].Answers["mode"].ToArray());
        Assert.IsFalse(projected.Requests["input-1"].Answers.ContainsKey("token"));

        var cleaned = BridgeStoreBusinessStateMerger.Merge(
            merged,
            core.Sessions,
            core.Approvals,
            InputRegistryState.Empty);
        Assert.IsFalse(cleaned.Sessions.ExtensionData!.ContainsKey("pendingInputs"));
        Assert.AreEqual("keep-root", cleaned.Sessions.ExtensionData["futureRoot"].GetString());
    }

    private static BridgeStoreSnapshot Store()
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

    private static BridgeStoreSnapshot EmptyStore() => new(
        new BindingStoreDocument(),
        new SessionStoreDocument(),
        new RouteStoreDocument(),
        new ApprovalStoreDocument(),
        new SettingsStoreDocument(),
        new ControlTokenStoreDocument());
}
