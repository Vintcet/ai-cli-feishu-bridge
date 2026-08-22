using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActiveManagedHookIngressTests
{
    private static readonly string Cwd = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "managed-hook-ingress-project"));
    private static readonly string TerminalSecret = new('a', 64);

    [TestMethod]
    public async Task SessionStartClaimsPublishesCanonicalIdentityThenDrains()
    {
        var fixture = await Fixture.CreateAsync();
        await fixture.RegisterAsync("terminal-start", elevated: true);

        var response = await fixture.Ingress.HandleAsync(
            BridgeManagedIngressKind.SessionStart,
            SessionStart("session-start", "terminal-start", elevated: true),
            "trace-start");

        Assert.AreEqual(JsonValueKind.Object, response.ValueKind);
        Assert.AreEqual("terminal-start", fixture.Directory
            .FindClaimBySession("session-start")?.TerminalId);
        CollectionAssert.AreEqual(
            new[] { "publish:session.started", "drain:session-start" },
            fixture.Operations.ToArray());
        var started = fixture.Sink.Events.Single();
        Assert.AreEqual(
            "terminal-start",
            started.Payload.GetProperty("managedTerminalId").GetString());
        Assert.IsTrue(started.Payload.GetProperty("managedTerminalElevated").GetBoolean());
        Assert.IsTrue(started.Payload.GetProperty("managedByAssistant").GetBoolean());
        Assert.AreEqual("startup", started.Payload.GetProperty("source").GetString());
    }

    [TestMethod]
    public async Task ReadyHeartbeatRetriesDrainForClaimedSession()
    {
        var fixture = await Fixture.CreateAsync();
        await fixture.RegisterAsync("terminal-late-ready", elevated: false, ready: false);

        await fixture.Ingress.HandleAsync(
            BridgeManagedIngressKind.SessionStart,
            SessionStart("session-late-ready", "terminal-late-ready", elevated: false),
            "trace-late-ready-start");

        await fixture.RegisterAsync("terminal-late-ready", elevated: false, ready: true);

        CollectionAssert.AreEqual(
            new[]
            {
                "publish:session.started",
                "drain:session-late-ready",
                "drain:session-late-ready",
            },
            fixture.Operations.ToArray());
    }

    [TestMethod]
    public async Task ManagedHooksRejectMissingOrCrossWiredIdentity()
    {
        var fixture = await Fixture.CreateAsync();
        await fixture.RegisterAsync("terminal-owner", elevated: true);
        await fixture.Ingress.HandleAsync(
            BridgeManagedIngressKind.SessionStart,
            SessionStart("session-owner", "terminal-owner", elevated: true),
            "trace-owner");

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            fixture.Ingress.HandleAsync(
                BridgeManagedIngressKind.Activity,
                Activity("session-owner", terminalId: null, elevated: null),
                "trace-missing"));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            fixture.Ingress.HandleAsync(
                BridgeManagedIngressKind.Activity,
                Activity("session-owner", "terminal-owner", elevated: false),
                "trace-elevation"));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            fixture.Ingress.HandleAsync(
                BridgeManagedIngressKind.Activity,
                Activity("session-other", "terminal-owner", elevated: true),
                "trace-session"));

        Assert.AreEqual(1, fixture.Sink.Events.Count);
    }

    [TestMethod]
    public async Task ExternalSessionWithoutManagedIdentityRemainsSupported()
    {
        var fixture = await Fixture.CreateAsync();

        await fixture.Ingress.HandleAsync(
            BridgeManagedIngressKind.SessionStart,
            SessionStart("external-session", terminalId: null, elevated: null),
            "trace-external");

        Assert.IsNull(fixture.Directory.FindClaimBySession("external-session"));
        Assert.AreEqual(RuntimeEventTypes.SessionStarted, fixture.Sink.Events.Single().EventType);
        CollectionAssert.Contains(
            fixture.Operations.ToArray(),
            "drain:external-session");
    }

    [TestMethod]
    public async Task RequestUserInputCannotEnterThroughActivityRoute()
    {
        var fixture = await Fixture.CreateAsync();
        var hook = JsonSerializer.SerializeToElement(new
        {
            hook_event_name = "PreToolUse",
            session_id = "external-input",
            turn_id = "turn-input",
            tool_use_id = "tool-input",
            cwd = Cwd,
            tool_name = "request_user_input",
            tool_input = new
            {
                questions = new[]
                {
                    new { id = "q1", question = "继续吗？" },
                },
            },
        });

        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            fixture.Ingress.HandleAsync(
                BridgeManagedIngressKind.Activity,
                hook,
                "trace-wrong-route"));

        Assert.AreEqual(0, fixture.Sink.Events.Count);
    }

    [TestMethod]
    public async Task SessionEndAndUnregisterReleaseOnlyAfterDurableEndEvent()
    {
        var fixture = await Fixture.CreateAsync();
        await fixture.RegisterAsync("terminal-ended", elevated: false);
        await fixture.Ingress.HandleAsync(
            BridgeManagedIngressKind.SessionStart,
            SessionStart("session-ended", "terminal-ended", elevated: false),
            "trace-start-ended");

        await fixture.Ingress.HandleAsync(
            BridgeManagedIngressKind.SessionEnd,
            SessionEnd("session-ended", "terminal-ended", elevated: false),
            "trace-end");

        Assert.IsNull(fixture.Directory.FindClaimBySession("session-ended"));
        Assert.AreEqual(RuntimeEventTypes.SessionEnded, fixture.Sink.Events.Last().EventType);

        await fixture.RegisterAsync("terminal-closed", elevated: true);
        await fixture.Ingress.HandleAsync(
            BridgeManagedIngressKind.SessionStart,
            SessionStart("session-closed", "terminal-closed", elevated: true),
            "trace-start-closed");
        await fixture.Ingress.HandleAsync(
            BridgeManagedIngressKind.TerminalUnregister,
            JsonSerializer.SerializeToElement(new { terminalId = "terminal-closed" }),
            "trace-unregister");

        Assert.IsNull(fixture.Directory.FindClaimBySession("session-closed"));
        Assert.AreEqual(RuntimeEventTypes.SessionEnded, fixture.Sink.Events.Last().EventType);
        Assert.AreEqual(
            "managed_terminal_unregistered",
            fixture.Sink.Events.Last().Payload.GetProperty("reason").GetString());
    }

    [TestMethod]
    public async Task SessionEndReleasesPendingInteractionOnlyAfterDurableEvent()
    {
        var fixture = await Fixture.CreateAsync();
        await fixture.RegisterAsync("terminal-pending", elevated: false);
        await fixture.Ingress.HandleAsync(
            BridgeManagedIngressKind.SessionStart,
            SessionStart("session-pending", "terminal-pending", elevated: false),
            "trace-start-pending");
        var pending = fixture.Ingress.HandleAsync(
            BridgeManagedIngressKind.Permission,
            Permission("session-pending", "terminal-pending", elevated: false),
            "trace-permission-pending");
        await WaitUntilAsync(() => fixture.Sink.Events.Count == 2);
        fixture.Sink.NextError = new IOException("simulated end store failure");

        await Assert.ThrowsExceptionAsync<IOException>(() =>
            fixture.Ingress.HandleAsync(
                BridgeManagedIngressKind.SessionEnd,
                SessionEnd("session-pending", "terminal-pending", elevated: false),
                "trace-end-failed"));

        Assert.IsFalse(pending.IsCompleted);
        Assert.IsNotNull(fixture.Directory.FindClaimBySession("session-pending"));

        await fixture.Ingress.HandleAsync(
            BridgeManagedIngressKind.SessionEnd,
            SessionEnd("session-pending", "terminal-pending", elevated: false),
            "trace-end-retry");

        Assert.AreEqual(0, (await pending).EnumerateObject().Count());
        Assert.IsNull(fixture.Directory.FindClaimBySession("session-pending"));
        Assert.AreEqual(RuntimeEventTypes.SessionEnded, fixture.Sink.Events.Last().EventType);
    }

    [TestMethod]
    public async Task InteractiveHookCancellationReleasesHttpWaiter()
    {
        var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var handling = fixture.Ingress.HandleAsync(
            BridgeManagedIngressKind.Permission,
            JsonSerializer.SerializeToElement(new
            {
                hook_event_name = "PermissionRequest",
                session_id = "external-interaction",
                turn_id = "turn-1",
                tool_use_id = "tool-1",
                cwd = Cwd,
                model = "gpt-5",
                tool_name = "shell_command",
                tool_input = new { command = "git status" },
            }),
            "trace-interaction",
            cancellation.Token);
        await fixture.Sink.FirstPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => handling);
        Assert.AreEqual(RuntimeEventTypes.ApprovalRequested, fixture.Sink.Events.Last().EventType);
    }

    [TestMethod]
    public async Task FailedPublishReleasesClaimAndAllowsFullRetry()
    {
        var fixture = await Fixture.CreateAsync();
        await fixture.RegisterAsync("terminal-retry", elevated: false);
        fixture.Sink.NextError = new IOException("simulated store failure");
        var hook = SessionStart("session-retry", "terminal-retry", elevated: false);

        await Assert.ThrowsExceptionAsync<IOException>(() =>
            fixture.Ingress.HandleAsync(
                BridgeManagedIngressKind.SessionStart,
                hook,
                "trace-failed"));
        Assert.IsNull(fixture.Directory.FindClaimBySession("session-retry"));

        await fixture.Ingress.HandleAsync(
            BridgeManagedIngressKind.SessionStart,
            hook,
            "trace-retry");

        Assert.AreEqual(2, fixture.Sink.PublishAttempts);
        Assert.AreEqual(1, fixture.Sink.Events.Count);
        Assert.AreEqual("trace-retry", fixture.Sink.Events.Single().TraceId);
    }

    [TestMethod]
    public async Task ActivityAcknowledgementDoesNotWaitForSlowBusinessPipeline()
    {
        var fixture = await Fixture.CreateAsync();
        await fixture.Ingress.HandleAsync(
            BridgeManagedIngressKind.SessionStart,
            SessionStart("activity-fast-ack", terminalId: null, elevated: null),
            "trace-fast-start");
        var release = fixture.Sink.BlockNextPublish();
        var startedAt = DateTime.UtcNow;

        var response = await fixture.Ingress.HandleAsync(
            BridgeManagedIngressKind.Activity,
            Activity(
                "activity-fast-ack",
                terminalId: null,
                elevated: null,
                turnId: "turn-fast"),
            "trace-fast-activity");

        Assert.AreEqual(JsonValueKind.Object, response.ValueKind);
        Assert.IsTrue(
            DateTime.UtcNow - startedAt < TimeSpan.FromMilliseconds(500),
            "Activity HTTP acknowledgement waited for the blocked event sink.");
        await fixture.Sink.WaitUntilPublishBlockedAsync();
        Assert.AreEqual(1, fixture.Sink.Events.Count);
        release.TrySetResult();
        await WaitUntilAsync(() => fixture.Sink.Events.Count == 2);
    }

    [TestMethod]
    public async Task StopFlushesQueuedActivityBeforeCompletionEvent()
    {
        var fixture = await Fixture.CreateAsync();
        await fixture.Ingress.HandleAsync(
            BridgeManagedIngressKind.SessionStart,
            SessionStart("activity-before-stop", terminalId: null, elevated: null),
            "trace-order-start");
        var release = fixture.Sink.BlockNextPublish();
        await fixture.Ingress.HandleAsync(
            BridgeManagedIngressKind.Activity,
            Activity(
                "activity-before-stop",
                terminalId: null,
                elevated: null,
                turnId: "turn-order"),
            "trace-order-activity");
        await fixture.Sink.WaitUntilPublishBlockedAsync();

        var stop = fixture.Ingress.HandleAsync(
            BridgeManagedIngressKind.Stop,
            Stop("activity-before-stop", "turn-order"),
            "trace-order-stop");
        await Task.Delay(100);
        Assert.IsFalse(stop.IsCompleted);

        release.TrySetResult();
        await stop;

        CollectionAssert.AreEqual(
            new[]
            {
                RuntimeEventTypes.SessionStarted,
                RuntimeEventTypes.TurnStarted,
                RuntimeEventTypes.TurnCompleted,
            },
            fixture.Sink.Events.Select(item => item.EventType).ToArray());
    }

    [TestMethod]
    public async Task ActivityQueueRemainsBoundedAndKeepsLatestProgress()
    {
        var fixture = await Fixture.CreateAsync(activityQueueCapacity: 2);
        await fixture.Ingress.HandleAsync(
            BridgeManagedIngressKind.SessionStart,
            SessionStart("activity-pressure", terminalId: null, elevated: null),
            "trace-pressure-start");
        var release = fixture.Sink.BlockNextPublish();
        await fixture.Ingress.HandleAsync(
            BridgeManagedIngressKind.Activity,
            Activity(
                "activity-pressure",
                terminalId: null,
                elevated: null,
                turnId: "turn-0"),
            "trace-pressure-0");
        await fixture.Sink.WaitUntilPublishBlockedAsync();

        for (var index = 1; index <= 20; index++)
        {
            await fixture.Ingress.HandleAsync(
                BridgeManagedIngressKind.Activity,
                Activity(
                    "activity-pressure",
                    terminalId: null,
                    elevated: null,
                    turnId: $"turn-{index}"),
                $"trace-pressure-{index}");
        }
        release.TrySetResult();
        await fixture.Ingress.HandleAsync(
            BridgeManagedIngressKind.Stop,
            Stop("activity-pressure", "turn-20"),
            "trace-pressure-stop");

        var activities = fixture.Sink.Events
            .Where(item => item.EventType == RuntimeEventTypes.TurnStarted)
            .ToArray();
        Assert.IsTrue(
            activities.Length <= 3,
            $"Bounded queue published {activities.Length} activity events.");
        Assert.IsTrue(activities.Any(item =>
            item.Payload.GetProperty("turnId").GetString() == "turn-20"));
    }

    private static JsonElement SessionStart(
        string sessionId,
        string? terminalId,
        bool? elevated)
    {
        var values = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "SessionStart",
            ["session_id"] = sessionId,
            ["cwd"] = Cwd,
            ["model"] = "gpt-5",
            ["source"] = "startup",
            ["runtime"] = RuntimeNames.Codex,
        };
        if (terminalId is not null)
        {
            values["managed_terminal_id"] = terminalId;
        }
        if (elevated is not null)
        {
            values["managed_terminal_elevated"] = elevated;
        }
        return JsonSerializer.SerializeToElement(values);
    }

    private static JsonElement SessionEnd(
        string sessionId,
        string terminalId,
        bool elevated) => JsonSerializer.SerializeToElement(new
        {
            hook_event_name = "SessionEnd",
            session_id = sessionId,
            cwd = Cwd,
            reason = "logout",
            runtime = RuntimeNames.Codex,
            managed_terminal_id = terminalId,
            managed_terminal_elevated = elevated,
        });

    private static JsonElement Activity(
        string sessionId,
        string? terminalId,
        bool? elevated,
        string? turnId = null)
    {
        var values = new Dictionary<string, object?>
        {
            ["hook_event_name"] = "UserPromptSubmit",
            ["session_id"] = sessionId,
            ["cwd"] = Cwd,
            ["runtime"] = RuntimeNames.Codex,
        };
        if (turnId is not null)
        {
            values["turn_id"] = turnId;
            values["prompt"] = $"prompt-{turnId}";
        }
        if (terminalId is not null)
        {
            values["managed_terminal_id"] = terminalId;
        }
        if (elevated is not null)
        {
            values["managed_terminal_elevated"] = elevated;
        }
        return JsonSerializer.SerializeToElement(values);
    }

    private static JsonElement Stop(string sessionId, string turnId) =>
        JsonSerializer.SerializeToElement(new
        {
            hook_event_name = "Stop",
            session_id = sessionId,
            turn_id = turnId,
            cwd = Cwd,
            model = "gpt-5",
            last_assistant_message = "done",
            runtime = RuntimeNames.Codex,
        });

    private static JsonElement Permission(
        string sessionId,
        string terminalId,
        bool elevated) => JsonSerializer.SerializeToElement(new
        {
            hook_event_name = "PermissionRequest",
            session_id = sessionId,
            turn_id = "turn-pending",
            tool_use_id = "tool-pending",
            cwd = Cwd,
            model = "gpt-5",
            tool_name = "shell_command",
            tool_input = new { command = "git status" },
            runtime = RuntimeNames.Codex,
            managed_terminal_id = terminalId,
            managed_terminal_elevated = elevated,
        });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeoutAt)
            {
                throw new AssertFailedException("等待测试条件超时。");
            }
            await Task.Delay(10);
        }
    }

    private sealed class Fixture(
        ActiveManagedHookIngress ingress,
        ActiveManagedTerminalDirectory directory,
        RecordingRuntimeEventSink sink,
        ConcurrentQueue<string> operations)
    {
        public ActiveManagedHookIngress Ingress { get; } = ingress;
        public ActiveManagedTerminalDirectory Directory { get; } = directory;
        public RecordingRuntimeEventSink Sink { get; } = sink;
        public ConcurrentQueue<string> Operations { get; } = operations;

        public static async Task<Fixture> CreateAsync(int activityQueueCapacity = 4)
        {
            var options = new BridgeHostOptions(
                Path.GetTempPath(),
                IPAddress.Loopback,
                0,
                BridgeOwnershipMode.Active,
                "managed-hook-test");
            var directory = new ActiveManagedTerminalDirectory(
                options,
                new RecordingStoreOwner());
            await directory.StartAsync(CancellationToken.None);
            var operations = new ConcurrentQueue<string>();
            var sink = new RecordingRuntimeEventSink(operations);
            var bridge = new ManagedRuntimeHookBridge(
                new ManagedRuntimeHookNormalizer(),
                sink,
                activityQueueCapacity: activityQueueCapacity);
            var ingress = new ActiveManagedHookIngress(
                options,
                directory,
                new RecordingLaunchCoordinator(operations),
                bridge);
            return new(ingress, directory, sink, operations);
        }

        public async Task RegisterAsync(
            string terminalId,
            bool elevated,
            bool ready = true) =>
            _ = await Ingress.HandleAsync(
                BridgeManagedIngressKind.TerminalRegister,
                JsonSerializer.SerializeToElement(new
                {
                    terminalId,
                    terminalSecret = TerminalSecret,
                    cwd = Cwd,
                    runtime = RuntimeNames.Codex,
                    elevated,
                    ready,
                }),
                $"trace-register-{terminalId}");
    }

    private sealed class RecordingRuntimeEventSink(
        ConcurrentQueue<string> operations) : IRuntimeEventSink
    {
        private readonly object blockSync = new();
        private TaskCompletionSource? blockedPublish;
        private TaskCompletionSource? nextPublishRelease;
        public List<RuntimeEventEnvelope> Events { get; } = [];
        public TaskCompletionSource FirstPublished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? NextError { get; set; }
        public int PublishAttempts { get; private set; }

        public TaskCompletionSource BlockNextPublish()
        {
            lock (blockSync)
            {
                blockedPublish = new(TaskCreationOptions.RunContinuationsAsynchronously);
                nextPublishRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return nextPublishRelease;
            }
        }

        public Task WaitUntilPublishBlockedAsync()
        {
            lock (blockSync)
            {
                return (blockedPublish ?? throw new InvalidOperationException(
                    "No publish was configured to block.")).Task;
            }
        }

        public async Task PublishAsync(
            RuntimeEventEnvelope runtimeEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublishAttempts++;
            if (NextError is { } error)
            {
                NextError = null;
                throw error;
            }
            Task? blocked = null;
            lock (blockSync)
            {
                if (nextPublishRelease is not null)
                {
                    blocked = nextPublishRelease.Task;
                    nextPublishRelease = null;
                    blockedPublish!.TrySetResult();
                }
            }
            if (blocked is not null)
            {
                await blocked.WaitAsync(cancellationToken);
            }
            Events.Add(runtimeEvent);
            operations.Enqueue($"publish:{runtimeEvent.EventType}");
            FirstPublished.TrySetResult();
        }
    }

    private sealed class RecordingLaunchCoordinator(
        ConcurrentQueue<string> operations) : IBridgeManagedRuntimeLaunchCoordinator
    {
        public BridgeManagedRuntimeLifecycleSnapshot Snapshot { get; } = new(0, 0, 0, 0);
        public BridgeManagedRuntimeLaunchRequest? Claim() => null;
        public BridgeManagedRuntimeLaunchCompletionResult Complete(
            BridgeManagedRuntimeLaunchCompletion completion) => new(true);
        public Task DrainAsync(
            string sessionExternalId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operations.Enqueue($"drain:{sessionExternalId}");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStoreOwner : IBridgeProductionStoreOwner
    {
        private readonly BridgeStoreSnapshot store = new(
            new BindingStoreDocument(),
            new SessionStoreDocument(),
            new RouteStoreDocument(),
            new ApprovalStoreDocument(),
            new SettingsStoreDocument(),
            new ControlTokenStoreDocument());

        public BridgeProductionStoreSnapshot Snapshot { get; } = new(
            BridgeProductionStoreState.Open,
            null,
            0);
        public ValueTask OpenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
        public ValueTask<BridgeStoreSnapshot> ReadAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(store);
        public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
        public ValueTask UpdateAsync(
            Func<BridgeStoreSnapshot, BridgeStoreSnapshot> update,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask CloseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
