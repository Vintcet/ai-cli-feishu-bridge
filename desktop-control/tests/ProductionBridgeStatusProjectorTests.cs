using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishuControl;

[TestClass]
public sealed class ProductionBridgeStatusProjectorTests
{
    [TestMethod]
    public void UsesBusinessOwnerAndLocalStoreWhenRedactedStoreHealthFailed()
    {
        var health = Health(
            Component("feishu-credentials", "ready"),
            Component("feishu-event-pump", "ready"),
            Component("production-store", "failed"));
        var control = Control(activeSessions: 1, pendingApprovals: 1);
        var bindings = new BindingStoreDocument
        {
            OwnerOpenId = "owner",
            PairingCode = "123456",
            Users =
            {
                ["owner"] = new BindingStoreRecord { OpenId = "owner" },
            },
        };
        var sessions = new SessionStoreDocument
        {
            Sessions =
            {
                ["session-active"] = Session(
                    "session-active",
                    "running",
                    new Dictionary<string, JsonElement>
                    {
                        ["alias"] = JsonSerializer.SerializeToElement("主窗口"),
                        ["model"] = JsonSerializer.SerializeToElement(new { id = "gpt-5.6" }),
                        ["managedTerminalId"] = JsonSerializer.SerializeToElement("terminal-1"),
                        ["managedTerminalElevated"] = JsonSerializer.SerializeToElement(true),
                        ["managedByAssistant"] = JsonSerializer.SerializeToElement(true),
                        ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-1"),
                        ["feishuChatName"] = JsonSerializer.SerializeToElement("主窗口群"),
                    }),
                ["session-history"] = Session(
                    "session-history",
                    "ended",
                    new Dictionary<string, JsonElement>
                    {
                        ["historyEligible"] = JsonSerializer.SerializeToElement(true),
                    }),
                ["session-hidden"] = Session(
                    "session-hidden",
                    "ended",
                    new Dictionary<string, JsonElement>
                    {
                        ["historyEligible"] = JsonSerializer.SerializeToElement(true),
                        ["historyHiddenAt"] = JsonSerializer.SerializeToElement(
                            "2026-08-09T05:00:00+08:00"),
                    }),
            },
        };
        var approvals = new ApprovalStoreDocument
        {
            Requests =
            {
                ["approval-1"] = new ApprovalStoreRecord
                {
                    RequestId = "approval-1",
                    SessionId = "session-active",
                    Cwd = @"K:\work\demo",
                    ToolName = "shell_command",
                    ToolPreview = "dotnet test",
                    CreatedAt = "2026-08-09T05:58:00+08:00",
                    ExpiresAt = "2026-08-09T06:08:00+08:00",
                    Status = "pending",
                    MessageIds = [],
                    ExtensionData = new Dictionary<string, JsonElement>
                    {
                        ["requiresManualApproval"] = JsonSerializer.SerializeToElement(true),
                        ["desktopApprovalRequested"] = JsonSerializer.SerializeToElement(true),
                    },
                },
                ["old-approval"] = new ApprovalStoreRecord
                {
                    RequestId = "old-approval",
                    SessionId = "session-history",
                    Cwd = @"K:\work\demo",
                    CreatedAt = "2026-08-09T04:00:00+08:00",
                    ExpiresAt = "2026-08-09T04:10:00+08:00",
                    Status = "resolved",
                    Resolution = "allow",
                    ResolvedAt = "2026-08-09T04:01:00+08:00",
                },
            },
        };
        var settings = new SettingsStoreDocument
        {
            WorkspaceRoot = @"K:\work",
            NotifyActivity = true,
            RetryMaxAttempts = 7,
        };

        var status = ProductionBridgeStatusProjector.Project(
            health,
            control,
            Presence("session-active", managedOnline: true, managedReady: true),
            bindings,
            sessions,
            approvals,
            settings,
            @"K:\default",
            "绑定",
            DateTimeOffset.Parse("2026-08-09T06:00:00+08:00"));

        Assert.IsTrue(status.Ok, "The failed redacted Store observer must not hide a ready owner.");
        Assert.AreEqual("connected", status.Feishu.State);
        Assert.AreEqual(1, status.Bindings);
        Assert.IsTrue(status.OwnerConfigured);
        Assert.AreEqual("绑定 123456", status.BindingCommand);
        Assert.AreEqual(1, status.ActiveSessions);
        Assert.AreEqual(1, status.Sessions.Count);
        Assert.AreEqual(1, status.HistorySessions.Count);
        Assert.AreEqual("session-history", status.HistorySessions[0].SessionId);
        Assert.AreEqual("主窗口", status.Sessions[0].Alias);
        Assert.AreEqual("gpt-5.6", status.Sessions[0].Model);
        Assert.IsTrue(status.Sessions[0].ManagedTerminalOnline);
        Assert.AreEqual("connected", status.Sessions[0].FeishuChatStatus);
        Assert.AreEqual(1, status.PendingApprovals);
        Assert.AreEqual(1, status.PendingDesktopApprovals);
        Assert.AreEqual(1, status.Approvals.Count);
        Assert.AreEqual("@主窗口 · demo #onactive", status.Approvals[0].SessionLabel);
        Assert.IsTrue(status.Settings.NotifyActivity);
        Assert.AreEqual(7, status.Settings.RetryMaxAttempts);
    }

    [TestMethod]
    public void EventPumpFailureIsNotReportedAsConnected()
    {
        var health = Health(
            Component("feishu-credentials", "ready"),
            Component("feishu-event-pump", "failed"));
        var status = ProductionBridgeStatusProjector.Project(
            health,
            Control(activeSessions: 0, pendingApprovals: 0),
            Presence(),
            new BindingStoreDocument(),
            new SessionStoreDocument(),
            new ApprovalStoreDocument(),
            new SettingsStoreDocument(),
            @"K:\default",
            "绑定",
            DateTimeOffset.Parse("2026-08-09T06:00:00+08:00"));

        Assert.AreEqual("failed", status.Feishu.State);
    }

    [TestMethod]
    public void ExpiredPendingApprovalIsNotProjectedAsActionableDesktopWork()
    {
        var approvals = new ApprovalStoreDocument
        {
            Requests =
            {
                ["approval-expired"] = new ApprovalStoreRecord
                {
                    RequestId = "approval-expired",
                    SessionId = "session-active",
                    Cwd = @"K:\work\demo",
                    CreatedAt = "2026-08-09T05:40:00+08:00",
                    ExpiresAt = "2026-08-09T05:59:00+08:00",
                    Status = "pending",
                    MessageIds = [],
                    ExtensionData = new Dictionary<string, JsonElement>
                    {
                        ["desktopApprovalRequested"] =
                            JsonSerializer.SerializeToElement(true),
                    },
                },
            },
        };

        var status = ProductionBridgeStatusProjector.Project(
            Health(
                Component("feishu-credentials", "ready"),
                Component("feishu-event-pump", "ready")),
            Control(activeSessions: 0, pendingApprovals: 1),
            Presence(),
            new BindingStoreDocument(),
            new SessionStoreDocument(),
            approvals,
            new SettingsStoreDocument(),
            @"K:\default",
            "绑定",
            DateTimeOffset.Parse("2026-08-09T06:00:00+08:00"));

        Assert.AreEqual(0, status.PendingDesktopApprovals);
        Assert.AreEqual(0, status.Approvals.Count);
    }

    [TestMethod]
    public void RejectsMixedHealthAndControlIdentities()
    {
        var health = Health(Component("feishu-event-pump", "ready"));
        var control = Control(activeSessions: 0, pendingApprovals: 0);
        control.ProcessId++;

        Assert.ThrowsException<InvalidDataException>(() =>
            ProductionBridgeStatusProjector.Project(
                health,
                control,
                Presence(),
                new BindingStoreDocument(),
                new SessionStoreDocument(),
                new ApprovalStoreDocument(),
                new SettingsStoreDocument(),
                @"K:\default",
                "绑定",
                DateTimeOffset.Parse("2026-08-09T06:00:00+08:00")));
    }

    [TestMethod]
    public void UnavailableHostPresenceFallsBackToVerifiedProcessInsteadOfStoreAggregate()
    {
        var sessions = new SessionStoreDocument
        {
            Sessions =
            {
                ["session-live"] = Session(
                    "session-live",
                    "running",
                    new Dictionary<string, JsonElement>
                    {
                        ["clientProcessId"] = JsonSerializer.SerializeToElement(4321),
                        ["clientProcessStartedAt"] = JsonSerializer.SerializeToElement(
                            "2026-08-09T05:30:00+08:00"),
                    }),
                ["session-stale"] = new SessionStoreRecord
                {
                    SessionId = "session-stale",
                    ShortId = "stale001",
                    Cwd = @"K:\work\stale",
                    ProjectName = "stale",
                    Status = "running",
                    Runtime = "codex",
                    OpenedAt = "2026-08-08T05:00:00+08:00",
                    LastSeenAt = "2026-08-08T05:00:00+08:00",
                    ExtensionData = new Dictionary<string, JsonElement>
                    {
                        ["historyEligible"] = JsonSerializer.SerializeToElement(true),
                    },
                },
            },
        };

        var status = ProductionBridgeStatusProjector.Project(
            Health(
                Component("feishu-credentials", "ready"),
                Component("feishu-event-pump", "ready")),
            Control(activeSessions: 130, pendingApprovals: 0),
            new BridgeProductionPresenceStatus(),
            new BindingStoreDocument(),
            sessions,
            new ApprovalStoreDocument(),
            new SettingsStoreDocument(),
            @"K:\default",
            "绑定",
            DateTimeOffset.Parse("2026-08-09T06:00:00+08:00"),
            (processId, startedAt) =>
                processId == 4321 &&
                startedAt == "2026-08-09T05:30:00+08:00");

        Assert.IsTrue(status.Ok);
        Assert.AreEqual("connected", status.Feishu.State);
        Assert.AreEqual(1, status.ActiveSessions);
        Assert.AreEqual("session-live", status.Sessions.Single().SessionId);
        Assert.AreEqual("session-stale", status.HistorySessions.Single().SessionId);
    }

    private static BridgeProductionHealthStatus Health(
        params BridgeProductionComponentStatus[] components) => new()
    {
        Ok = false,
        HostKind = "dotnet",
        ManagementApiVersion = 1,
        InstanceName = "production-dotnet",
        Status = "ready",
        Version = "0.19.1.0",
        ProcessId = 4321,
        StartedAt = "2026-08-09T05:30:00+08:00",
        OwnershipMode = "active",
        ActiveOwner = true,
        Components = [.. components],
    };

    private static BridgeProductionComponentStatus Component(
        string name,
        string status) => new()
    {
        Name = name,
        Status = status,
    };

    private static BridgeProductionPresenceStatus Presence(
        string? sessionId = null,
        bool managedOnline = false,
        bool managedReady = false) => new()
    {
        Ok = true,
        Sessions = sessionId is null
            ? []
            :
            [
                new BridgeProductionSessionPresence
                {
                    SessionId = sessionId,
                    ManagedTerminalOnline = managedOnline,
                    ManagedTerminalReady = managedReady,
                },
            ],
    };

    private static BridgeProductionControlStatus Control(
        int activeSessions,
        int pendingApprovals) => new()
    {
        Ok = false,
        HostKind = "dotnet",
        ManagementApiVersion = 1,
        InstanceName = "production-dotnet",
        Lifecycle = "ready",
        ProcessId = 4321,
        OwnershipMode = "active",
        ActiveOwner = true,
        BusinessState = new()
        {
            Initialized = true,
            Sessions = activeSessions + 2,
            ActiveSessions = activeSessions,
            EndedSessions = 2,
            Approvals = pendingApprovals,
            PendingApprovals = pendingApprovals,
        },
        Boundaries = new()
        {
            FeishuAdapter = new()
            {
                LiveEventStreamEnabled = true,
                OutboundMessagingEnabled = true,
            },
        },
    };

    private static SessionStoreRecord Session(
        string sessionId,
        string status,
        Dictionary<string, JsonElement> extensionData) => new()
    {
        SessionId = sessionId,
        ShortId = sessionId == "session-active" ? "onactive" : "history1",
        Cwd = @"K:\work\demo",
        ProjectName = "demo",
        Status = status,
        Runtime = "codex",
        OpenedAt = "2026-08-09T05:00:00+08:00",
        LastSeenAt = "2026-08-09T05:55:00+08:00",
        EndedAt = status == "ended" ? "2026-08-09T05:56:00+08:00" : null,
        ExtensionData = extensionData,
    };
}
