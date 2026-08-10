using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.OpenCode;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class BridgeDesktopSessionPresenceTests
{
    [TestMethod]
    public void ProjectsOnlyActuallyReachableSessions()
    {
        var now = DateTimeOffset.Parse("2026-08-09T06:30:00+08:00");
        var store = Store(
            Session("external-live", "codex", "running", now, new()
            {
                ["clientProcessId"] = JsonSerializer.SerializeToElement(101),
                ["clientProcessStartedAt"] = JsonSerializer.SerializeToElement(
                    "2026-08-09T06:00:00+08:00"),
            }),
            Session("external-dead", "codex", "waiting", now, new()
            {
                ["clientProcessId"] = JsonSerializer.SerializeToElement(102),
            }),
            Session("managed-live", "claudecode", "waiting", now, new()
            {
                ["managedTerminalId"] = JsonSerializer.SerializeToElement("terminal-live"),
            }),
            Session("managed-dead", "codex", "waiting", now, new()
            {
                ["managedTerminalId"] = JsonSerializer.SerializeToElement("terminal-dead"),
            }),
            Session("opencode-live", "opencode", "running", now, new()),
            Session("opencode-dead", "opencode", "waiting", now, new()),
            Session("fallback-recent", "codex", "running", now - TimeSpan.FromMinutes(2), new()),
            Session("fallback-stale", "codex", "waiting", now - TimeSpan.FromMinutes(6), new()),
            Session("heartbeat-live", "codex", "waiting", now - TimeSpan.FromHours(1), new()),
            Session("ended", "codex", "ended", now, new()));
        var terminals = new RecordingTerminalDirectory
        {
            Targets =
            {
                ["managed-live"] = new("terminal-live", "managed-live", Ready: true),
            },
        };
        var openCode = new RecordingOpenCodeDirectory
        {
            Endpoints =
            {
                ["opencode-live"] = new(new Uri("http://127.0.0.1:5100"), null),
            },
        };

        var result = BridgeDesktopSessionPresenceProjection.Project(
            store,
            terminals,
            openCode,
            now,
            (processId, _) => processId == 101,
            sessionId => sessionId is "external-dead" or "heartbeat-live");

        Assert.IsTrue(result.Ok);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "external-live",
                "external-dead",
                "fallback-recent",
                "heartbeat-live",
                "managed-live",
                "opencode-live",
            },
            result.Sessions.Select(session => session.SessionId).ToArray());
        var managed = result.Sessions.Single(session => session.SessionId == "managed-live");
        Assert.IsTrue(managed.ManagedTerminalOnline);
        Assert.IsTrue(managed.ManagedTerminalReady);
        Assert.IsFalse(result.Sessions.Single(session =>
            session.SessionId == "external-live").ManagedTerminalOnline);
    }

    [TestMethod]
    public void LocalHeartbeatDirectoryRejectsDeadProcessesAndExpiresThem()
    {
        var online = true;
        var directory = new BridgeDesktopSessionHeartbeatDirectory(
            (processId, startedAt) =>
                online &&
                processId == 456 &&
                startedAt == "2026-08-09T08:00:00Z");
        using var payload = JsonDocument.Parse("""
            {
              "session_id": "session-local",
              "client_process_id": 456,
              "client_process_started_at": "2026-08-09T08:00:00Z"
            }
            """);

        var recorded = directory.Record(payload.RootElement);

        Assert.AreEqual("session-local", recorded.Heartbeat.SessionId);
        Assert.IsTrue(recorded.Changed);
        Assert.IsTrue(directory.IsOnline("session-local"));
        online = false;
        Assert.IsFalse(directory.IsOnline("session-local"));
        Assert.IsFalse(directory.IsOnline("session-local"));
    }

    [TestMethod]
    public void ConfiguredSessionLifetimeNarrowsOnlyTheFallbackWindow()
    {
        var now = DateTimeOffset.Parse("2026-08-09T06:30:00+08:00");
        var store = Store(
            Session("fallback-recent", "codex", "waiting", now - TimeSpan.FromSeconds(30), new()),
            Session("fallback-stale", "codex", "waiting", now - TimeSpan.FromMinutes(2), new()),
            Session("process-live", "codex", "waiting", now - TimeSpan.FromHours(1), new()
            {
                ["clientProcessId"] = JsonSerializer.SerializeToElement(101),
            }));

        var result = BridgeDesktopSessionPresenceProjection.Project(
            store,
            new RecordingTerminalDirectory(),
            new RecordingOpenCodeDirectory(),
            now,
            (processId, _) => processId == 101,
            sessionActiveLifetime: TimeSpan.FromMinutes(1));

        CollectionAssert.AreEquivalent(
            new[] { "fallback-recent", "process-live" },
            result.Sessions.Select(session => session.SessionId).ToArray());
    }

    [TestMethod]
    public void SessionLifetimeParserUsesPositiveMillisecondsOrDefault()
    {
        var fallback = TimeSpan.FromDays(1);

        Assert.AreEqual(
            TimeSpan.FromSeconds(45),
            BridgeLocalConfiguration.ParsePositiveMilliseconds("45000", fallback));
        Assert.AreEqual(
            fallback,
            BridgeLocalConfiguration.ParsePositiveMilliseconds("0", fallback));
        Assert.AreEqual(
            fallback,
            BridgeLocalConfiguration.ParsePositiveMilliseconds("invalid", fallback));
    }

    private static BridgeStoreSnapshot Store(params SessionStoreRecord[] sessions) => new(
        new BindingStoreDocument(),
        new SessionStoreDocument
        {
            Sessions = sessions.ToDictionary(
                session => session.SessionId,
                session => session,
                StringComparer.Ordinal),
        },
        new RouteStoreDocument(),
        new ApprovalStoreDocument(),
        new SettingsStoreDocument(),
        new ControlTokenStoreDocument());

    private static SessionStoreRecord Session(
        string sessionId,
        string runtime,
        string status,
        DateTimeOffset lastSeenAt,
        Dictionary<string, JsonElement> extensionData) => new()
    {
        SessionId = sessionId,
        Runtime = runtime,
        Status = status,
        Cwd = @"K:\work",
        LastSeenAt = lastSeenAt.ToString("O"),
        ExtensionData = extensionData,
    };

    private sealed class RecordingTerminalDirectory : IManagedTerminalDirectory
    {
        public Dictionary<string, ManagedTerminalTarget> Targets { get; } =
            new(StringComparer.Ordinal);

        public ManagedTerminalTarget? FindBySession(string sessionExternalId) =>
            Targets.GetValueOrDefault(sessionExternalId);
    }

    private sealed class RecordingOpenCodeDirectory : IOpenCodeEndpointDirectory
    {
        public Dictionary<string, OpenCodeEndpoint> Endpoints { get; } =
            new(StringComparer.Ordinal);

        public OpenCodeEndpoint? FindBySession(string sessionExternalId) =>
            Endpoints.GetValueOrDefault(sessionExternalId);

        public IReadOnlyList<OpenCodeEndpoint> ListReady() =>
            Endpoints.Values.Where(endpoint => endpoint.Ready).ToArray();
    }
}
