using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Encodings.Web;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActiveRuntimeRetryCoordinatorTests
{
    [TestMethod]
    public async Task ApprovalNotificationRunsOnlyAfterStatePersistenceAndIsBestEffort()
    {
        var actions = new ConcurrentQueue<string>();
        var notifier = new RecordingApprovalNotifier(actions);
        await using var fixture = await RetryFixture.CreateAsync(
            actions: actions,
            approvalNotifications: notifier);

        await fixture.Coordinator.HandleAsync(Event(
            "approval-requested",
            RuntimeEventTypes.ApprovalRequested,
            "turn-approval",
            new
            {
                requestId = "approval-1",
                title = "shell_command",
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(20).ToString("O"),
            }));

        CollectionAssert.AreEqual(
            new[]
            {
                "state:approval.requested",
                $"approval:approval-1:{SessionId}",
            },
            actions.ToArray());

        notifier.Error = new InvalidOperationException("synthetic delivery failure");
        await fixture.Coordinator.HandleAsync(Event(
            "approval-requested-failed-delivery",
            RuntimeEventTypes.ApprovalRequested,
            "turn-approval-2",
            new
            {
                requestId = "approval-2",
                title = "shell_command",
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(20).ToString("O"),
            }));

        Assert.IsTrue(fixture.State.Completed);
    }

    [TestMethod]
    public async Task TerminalApprovalSynchronizationRunsOnlyAfterStatePersistence()
    {
        var actions = new ConcurrentQueue<string>();
        var notifier = new RecordingApprovalNotifier(actions);
        await using var fixture = await RetryFixture.CreateAsync(
            actions: actions,
            approvalNotifications: notifier);

        await fixture.Coordinator.HandleAsync(Event(
            "approval-resolved",
            RuntimeEventTypes.ApprovalResolvedExternally,
            "turn-approval",
            new
            {
                requestId = "approval-1",
                resolution = ApprovalResolutions.Allow,
            }));
        await fixture.Coordinator.HandleAsync(Event(
            "session-ended",
            RuntimeEventTypes.SessionEnded,
            "turn-approval",
            new { }));

        CollectionAssert.AreEqual(
            new[]
            {
                "state:approval.resolved_externally",
                $"approval-sync:approval-1:{SessionId}",
                "state:session.ended",
                $"approval-session-sync:{SessionId}",
            },
            actions.ToArray());
    }

    [TestMethod]
    public async Task SessionStartSchedulesGroupOnlyAfterStatePersistence()
    {
        var actions = new ConcurrentQueue<string>();
        var sessionGroups = new RecordingSessionGroupCoordinator(actions);
        await using var fixture = await RetryFixture.CreateAsync(
            sessionGroups: sessionGroups,
            actions: actions);

        await fixture.Coordinator.HandleAsync(Event(
            "session-started",
            RuntimeEventTypes.SessionStarted,
            "session-started",
            new { }));

        CollectionAssert.AreEqual(
            new[] { "state:session.started", $"group:{SessionId}" },
            actions.ToArray());
    }

    [TestMethod]
    public async Task CompletionUsesSessionGroupNotificationRouter()
    {
        var sessionGroups = new RecordingSessionGroupCoordinator(
            chats: ["chat-session-group"]);
        await using var fixture = await RetryFixture.CreateAsync(
            sessionGroups: sessionGroups);

        await fixture.Coordinator.HandleAsync(Event(
            "completed-session-group",
            RuntimeEventTypes.TurnCompleted,
            "turn-session-group",
            new { turnId = "turn-session-group", message = "done" }));

        Assert.AreEqual("chat-session-group", fixture.Gateway.Sends.Single().ChatId);
        CollectionAssert.AreEqual(
            new[] { SessionId },
            sessionGroups.NotificationRequests.ToArray());
    }

    [TestMethod]
    public async Task RetryableFailurePersistsBeforeNotificationAndDispatchesStandardPrompt()
    {
        await using var fixture = await RetryFixture.CreateAsync(
            retryDelay: TimeSpan.FromMilliseconds(100));

        await fixture.Coordinator.HandleAsync(Failure("turn-1", "HTTP 429"));

        CollectionAssert.AreEqual(
            new[] { "state:turn.failed", "send:chat-1" },
            fixture.Actions.ToArray());
        Assert.AreEqual("sent", ExtensionString(
            fixture.Store.Current.Sessions.Sessions[SessionId],
            "lastNotificationStatus"));
        Assert.AreEqual("error", fixture.Store.Current.Routes.Messages
            .Values.Single().Kind);
        Assert.IsTrue(fixture.Coordinator.HasActiveRetry(SessionId));

        var command = await fixture.RuntimeCommands.Dispatched.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        Assert.AreEqual(RuntimeCommandTypes.PromptSend, command.CommandType);
        Assert.AreEqual(RuntimeNames.Codex, command.Runtime);
        Assert.AreEqual(SessionId, command.Session!.ExternalId);
        Assert.AreEqual("steer", command.Payload.GetProperty("mode").GetString());
        StringAssert.Contains(command.Payload.GetProperty("prompt").GetString()!, "临时服务错误");
        StringAssert.StartsWith(command.CommandId, "runtime-retry-");
        StringAssert.EndsWith(command.CommandId, "-1");
        Assert.IsTrue(BridgeProtocolValidator.Validate(command).IsValid);
        CollectionAssert.AreEqual(
            new[] { "state:turn.failed", "send:chat-1", "dispatch" },
            fixture.Actions.ToArray());

        await fixture.Coordinator.HandleAsync(Event(
            "completed-1",
            RuntimeEventTypes.TurnCompleted,
            "turn-1",
            new { }));
        Assert.IsFalse(fixture.Coordinator.HasActiveRetry(SessionId));
    }

    [TestMethod]
    public async Task NonRetryableFailureAndUnavailableRuntimeOnlySendErrorCard()
    {
        await using var nonRetryable = await RetryFixture.CreateAsync(
            retryDelay: TimeSpan.FromMilliseconds(20));
        await nonRetryable.Coordinator.HandleAsync(Failure(
            "turn-permission",
            "permission denied"));

        await using var unavailable = await RetryFixture.CreateAsync(
            ready: false,
            retryDelay: TimeSpan.FromMilliseconds(20));
        await unavailable.Coordinator.HandleAsync(Failure(
            "turn-unavailable",
            "HTTP 503"));

        await Task.Delay(80);
        Assert.AreEqual(1, nonRetryable.Gateway.Sends.Count);
        Assert.AreEqual(0, nonRetryable.RuntimeCommands.Commands.Count);
        Assert.IsFalse(nonRetryable.Coordinator.HasActiveRetry(SessionId));
        Assert.AreEqual(1, unavailable.Gateway.Sends.Count);
        Assert.AreEqual(0, unavailable.RuntimeCommands.Commands.Count);
        Assert.IsFalse(unavailable.Coordinator.HasActiveRetry(SessionId));
    }

    [TestMethod]
    public async Task StopBeforeDueIsIdempotentAndWrongCycleFailsClosed()
    {
        await using var fixture = await RetryFixture.CreateAsync(
            retryDelay: TimeSpan.FromSeconds(5));
        await fixture.Coordinator.HandleAsync(Failure("turn-stop", "HTTP 502"));
        var sent = fixture.Gateway.Sends.Single();
        var cycleId = RequiredJsonString(sent.Card, "retryCycleId");

        var stopped = await fixture.Coordinator.StopAsync(
            SessionId,
            cycleId,
            sent.MessageId);
        var repeated = await fixture.Coordinator.StopAsync(
            SessionId,
            cycleId,
            sent.MessageId);
        var stale = await fixture.Coordinator.StopAsync(
            SessionId,
            "wrong-cycle",
            sent.MessageId);

        Assert.AreEqual(BridgeRetryStopKinds.Stopped, stopped.Kind);
        Assert.IsFalse(stopped.RetryAlreadyStarted);
        Assert.IsNotNull(stopped.Card);
        Assert.IsFalse(CardJson(stopped.Card).Contains(
            FeishuCardActions.RetryStop,
            StringComparison.Ordinal));
        Assert.AreEqual(BridgeRetryStopKinds.AlreadyStopped, repeated.Kind);
        Assert.AreEqual(BridgeRetryStopKinds.Stale, stale.Kind);
        Assert.AreEqual(0, fixture.RuntimeCommands.Commands.Count);
        Assert.IsFalse(fixture.Coordinator.HasActiveRetry(SessionId));
    }

    [TestMethod]
    public async Task StoppingRunningRetryPreventsEveryLaterAutomaticAttempt()
    {
        await using var fixture = await RetryFixture.CreateAsync(
            retryDelay: TimeSpan.FromMilliseconds(5));
        await fixture.Coordinator.HandleAsync(Failure("turn-running-1", "HTTP 502"));
        await fixture.RuntimeCommands.Dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var sent = fixture.Gateway.Sends.Single();
        var cycleId = RequiredJsonString(sent.Card, "retryCycleId");

        var stopped = await fixture.Coordinator.StopAsync(
            SessionId,
            cycleId,
            sent.MessageId);
        await fixture.Coordinator.HandleAsync(Failure("turn-running-2", "HTTP 503"));
        await Task.Delay(80);

        Assert.IsTrue(stopped.RetryAlreadyStarted);
        Assert.AreEqual(1, fixture.RuntimeCommands.Commands.Count);
        Assert.AreEqual(2, fixture.Gateway.Sends.Count);
        Assert.IsFalse(CardJson(fixture.Gateway.Sends[1].Card).Contains(
            FeishuCardActions.RetryStop,
            StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ManualTurnCompletionAndDisconnectCancelScheduledRetries()
    {
        await using var fixture = await RetryFixture.CreateAsync(
            retryDelay: TimeSpan.FromMilliseconds(250));

        await fixture.Coordinator.HandleAsync(Failure("turn-manual", "HTTP 502"));
        await fixture.Coordinator.BeginManualTurnAsync(SessionId);
        await fixture.Coordinator.HandleAsync(Failure("turn-completed", "HTTP 502"));
        await fixture.Coordinator.HandleAsync(Event(
            "completed",
            RuntimeEventTypes.TurnCompleted,
            "turn-completed",
            new { }));
        await fixture.Coordinator.HandleAsync(Failure("turn-disconnected", "HTTP 502"));
        await fixture.Coordinator.HandleAsync(Event(
            "disconnected",
            RuntimeEventTypes.RuntimeDisconnected,
            "turn-disconnected",
            new { }));

        await Task.Delay(350);
        Assert.AreEqual(0, fixture.RuntimeCommands.Commands.Count);
        Assert.IsFalse(fixture.Coordinator.HasActiveRetry(SessionId));
    }

    [TestMethod]
    public async Task ManualTurnWinsWhenItRacesFailureStatePersistence()
    {
        await using var fixture = await RetryFixture.CreateAsync(
            retryDelay: TimeSpan.FromMilliseconds(20));
        fixture.State.PersistenceGate = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var failure = fixture.Coordinator.HandleAsync(
            Failure("turn-race", "HTTP 502"));
        await fixture.State.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Coordinator.BeginManualTurnAsync(SessionId);
        fixture.State.PersistenceGate.SetResult();
        await failure;
        await Task.Delay(80);

        Assert.AreEqual(0, fixture.RuntimeCommands.Commands.Count);
        Assert.IsFalse(fixture.Coordinator.HasActiveRetry(SessionId));
    }

    [TestMethod]
    public async Task NoRecipientsStillRetryLocallyAndClearNotificationClaim()
    {
        await using var fixture = await RetryFixture.CreateAsync(
            chats: [],
            retryDelay: TimeSpan.FromMilliseconds(5));

        await fixture.Coordinator.HandleAsync(Failure("turn-local", "HTTP 503"));
        await fixture.RuntimeCommands.Dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(0, fixture.Gateway.Sends.Count);
        Assert.AreEqual(1, fixture.RuntimeCommands.Commands.Count);
        var session = fixture.Store.Current.Sessions.Sessions[SessionId];
        Assert.IsNull(ExtensionString(session, "lastNotificationStatus"));
        Assert.IsNull(ExtensionString(session, "pendingNotificationKind"));
    }

    [TestMethod]
    public async Task TurnCompletionPersistsStateBeforeSendingStopCard()
    {
        await using var fixture = await RetryFixture.CreateAsync();
        fixture.Gateway.BeforeSend = () =>
        {
            Assert.IsTrue(fixture.State.Completed);
            Assert.AreEqual(
                "pending",
                ExtensionString(
                    fixture.Store.Current.Sessions.Sessions[SessionId],
                    "lastNotificationStatus"));
        };

        await fixture.Coordinator.HandleAsync(Event(
            "completed-stop",
            RuntimeEventTypes.TurnCompleted,
            "turn-stop-card",
            new
            {
                turnId = "turn-stop-card",
                message = "任务已经完成。",
            }));

        CollectionAssert.AreEqual(
            new[] { "state:turn.completed", "send:chat-1" },
            fixture.Actions.ToArray());
        Assert.AreEqual(1, fixture.Gateway.Sends.Count);
        var idempotencyKey = fixture.Gateway.Attempts.Single().IdempotencyKey;
        Assert.AreEqual(32, idempotencyKey.Length);
        Assert.IsTrue(idempotencyKey.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f'));
        Assert.AreEqual("stop", fixture.Store.Current.Routes.Messages
            .Values.Single().Kind);
        Assert.AreEqual(
            "sent",
            ExtensionString(
                fixture.Store.Current.Sessions.Sessions[SessionId],
                "lastNotificationStatus"));
        Assert.AreEqual(
            "turn-stop-card",
            ExtensionString(
                fixture.Store.Current.Sessions.Sessions[SessionId],
                "lastNotificationTurnId"));
        Assert.IsNull(ExtensionString(
            fixture.Store.Current.Sessions.Sessions[SessionId],
            "pendingNotificationKind"));
        StringAssert.Contains(
            CardJson(fixture.Gateway.Sends.Single().Card),
            "Codex 本轮已完成");
        StringAssert.Contains(
            CardJson(fixture.Gateway.Sends.Single().Card),
            "任务已经完成");
    }

    [TestMethod]
    public async Task ExplicitFileReturnStripsDirectivesAndSendsRequestedFilesAfterCompletion()
    {
        await using var fixture = await RetryFixture.CreateAsync();
        fixture.FileTransfers.ObservePromptDispatch(
            SessionId,
            "chat-1",
            requestFileReturn: true,
            queued: false);

        await fixture.Coordinator.HandleAsync(Event(
            "completed-file-return",
            RuntimeEventTypes.TurnCompleted,
            "turn-file-return",
            new
            {
                turnId = "turn-file-return",
                message = "报告已生成。\nBRIDGE_SEND_FILE: \"K:\\project\\report.txt\"",
            }));

        Assert.AreEqual(1, fixture.FileTransfers.SentFiles.Count);
        Assert.AreEqual("chat-1", fixture.FileTransfers.SentFiles[0].ChatId);
        CollectionAssert.AreEqual(
            new[] { "K:\\project\\report.txt" },
            fixture.FileTransfers.SentFiles[0].Paths.ToArray());
        var cardJson = CardJson(fixture.Gateway.Sends.Single().Card);
        StringAssert.Contains(cardJson, "报告已生成");
        Assert.IsFalse(cardJson.Contains("BRIDGE_SEND_FILE", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DuplicateCompletionDoesNotConsumeAFileRequestForALaterTurn()
    {
        await using var fixture = await RetryFixture.CreateAsync();
        fixture.FileTransfers.ObservePromptDispatch(
            SessionId,
            "chat-first",
            requestFileReturn: true,
            queued: false);
        var first = Event(
            "completed-file-first",
            RuntimeEventTypes.TurnCompleted,
            "turn-file-first",
            new
            {
                turnId = "turn-file-first",
                message = "第一份\nBRIDGE_SEND_FILE: K:\\project\\first.txt",
            });
        await fixture.Coordinator.HandleAsync(first);

        fixture.FileTransfers.ObservePromptDispatch(
            SessionId,
            "chat-second",
            requestFileReturn: true,
            queued: false);
        await fixture.Coordinator.HandleAsync(first);
        Assert.AreEqual(1, fixture.FileTransfers.SentFiles.Count);

        await fixture.Coordinator.HandleAsync(Event(
            "completed-file-second",
            RuntimeEventTypes.TurnCompleted,
            "turn-file-second",
            new
            {
                turnId = "turn-file-second",
                message = "第二份\nBRIDGE_SEND_FILE: K:\\project\\second.txt",
            }));

        Assert.AreEqual(2, fixture.FileTransfers.SentFiles.Count);
        Assert.AreEqual("chat-second", fixture.FileTransfers.SentFiles[1].ChatId);
    }

    [TestMethod]
    public async Task DuplicateCompletionDoesNotSendAnotherStopCard()
    {
        await using var fixture = await RetryFixture.CreateAsync();
        var completed = Event(
            "completed-duplicate",
            RuntimeEventTypes.TurnCompleted,
            "turn-duplicate",
            new
            {
                turnId = "turn-duplicate",
                message = "只发送一次。",
            });

        await fixture.Coordinator.HandleAsync(completed);
        await fixture.Coordinator.HandleAsync(completed);

        Assert.AreEqual(1, fixture.Gateway.Sends.Count);
        Assert.AreEqual(1, fixture.Gateway.Attempts.Count);
        Assert.AreEqual(1, fixture.Store.Current.Routes.Messages.Count);
        Assert.AreEqual("stop", fixture.Store.Current.Routes.Messages
            .Values.Single().Kind);
    }

    [TestMethod]
    public async Task ErrorAndCompletionForTheSameTurnAreMutuallyExclusive()
    {
        await using var errorFirst = await RetryFixture.CreateAsync(
            autoRetry: false);
        await errorFirst.Coordinator.HandleAsync(Failure(
            "turn-mutual-error-first",
            "permission denied"));
        await errorFirst.Coordinator.HandleAsync(Event(
            "completion-after-error",
            RuntimeEventTypes.TurnCompleted,
            "turn-mutual-error-first",
            new { turnId = "turn-mutual-error-first", message = "后来完成。" }));

        Assert.AreEqual(1, errorFirst.Gateway.Sends.Count);
        Assert.AreEqual("error", errorFirst.Store.Current.Routes.Messages
            .Values.Single().Kind);
        Assert.IsFalse(CardJson(errorFirst.Gateway.Sends.Single().Card)
            .Contains("后来完成", StringComparison.Ordinal));

        await using var completionFirst = await RetryFixture.CreateAsync(
            autoRetry: false);
        await completionFirst.Coordinator.HandleAsync(Event(
            "completion-before-error",
            RuntimeEventTypes.TurnCompleted,
            "turn-mutual-completion-first",
            new
            {
                turnId = "turn-mutual-completion-first",
                message = "先完成。",
            }));
        await completionFirst.Coordinator.HandleAsync(Failure(
            "turn-mutual-completion-first",
            "HTTP 503"));
        await Task.Delay(40);

        Assert.AreEqual(1, completionFirst.Gateway.Sends.Count);
        Assert.AreEqual("stop", completionFirst.Store.Current.Routes.Messages
            .Values.Single().Kind);
        Assert.IsFalse(completionFirst.Coordinator.HasActiveRetry(SessionId));
    }

    [TestMethod]
    public async Task PendingCompletionRecoversPartialMultiChatDeliveryWithStableKey()
    {
        var gateway = new RecordingFeishuGateway();
        gateway.FailChats.Add("chat-2");
        var store = new RecordingStoreOwner(StoreSnapshot(
            ["chat-1", "chat-2"],
            autoRetry: false));

        await using (var first = await RetryFixture.CreateAsync(
            store: store,
            gateway: gateway,
            ready: false))
        {
            await first.Coordinator.HandleAsync(Event(
                "completion-partial",
                RuntimeEventTypes.TurnCompleted,
                "turn-partial-stop",
                new
                {
                    turnId = "turn-partial-stop",
                    message = "多聊天恢复。",
                }));
        }

        Assert.AreEqual(
            "pending",
            ExtensionString(
                store.Current.Sessions.Sessions[SessionId],
                "lastNotificationStatus"));
        var firstKey = gateway.Attempts.Single(attempt =>
            attempt.ChatId == "chat-1").IdempotencyKey;
        gateway.FailChats.Clear();

        await using (var recovered = await RetryFixture.CreateAsync(
            store: store,
            gateway: gateway,
            ready: false))
        {
            Assert.AreEqual(
                "sent",
                ExtensionString(
                    store.Current.Sessions.Sessions[SessionId],
                    "lastNotificationStatus"));
        }

        var recoveredKey = gateway.Attempts.Last(attempt =>
            attempt.ChatId == "chat-1").IdempotencyKey;
        Assert.AreEqual(firstKey, recoveredKey);
        Assert.AreEqual(2, store.Current.Routes.Messages.Count);
        Assert.IsTrue(store.Current.Routes.Messages.Values.All(route =>
            route.Kind == "stop"));
    }

    [TestMethod]
    public async Task PendingStopClaimRecoversOnStartup()
    {
        var store = new RecordingStoreOwner(WithPendingNotification(
            StoreSnapshot(["chat-1"], autoRetry: false),
            "turn-pending-stop",
            "stop",
            "启动后继续发送。"));
        var gateway = new RecordingFeishuGateway();

        await using var fixture = await RetryFixture.CreateAsync(
            store: store,
            gateway: gateway,
            ready: false);

        Assert.AreEqual(1, gateway.Sends.Count);
        Assert.AreEqual("stop", store.Current.Routes.Messages
            .Values.Single().Kind);
        Assert.AreEqual(
            "sent",
            ExtensionString(
                store.Current.Sessions.Sessions[SessionId],
                "lastNotificationStatus"));
        StringAssert.Contains(
            CardJson(gateway.Sends.Single().Card),
            "启动后继续发送");
    }

    [TestMethod]
    public async Task OpenCodeCompletionWithoutMessageUsesRuntimeFallback()
    {
        var store = new RecordingStoreOwner(StoreSnapshot(
            ["chat-1"],
            autoRetry: false,
            runtime: RuntimeNames.OpenCode));
        await using var fixture = await RetryFixture.CreateAsync(
            store: store,
            runtime: RuntimeNames.OpenCode,
            ready: false);

        await fixture.Coordinator.HandleAsync(Event(
            "opencode-idle",
            RuntimeEventTypes.TurnCompleted,
            "turn-opencode-idle",
            new { },
            runtime: RuntimeNames.OpenCode));

        Assert.AreEqual(1, fixture.Gateway.Sends.Count);
        StringAssert.Contains(
            CardJson(fixture.Gateway.Sends.Single().Card),
            "OpenCode 已结束本轮处理");
        Assert.AreEqual("stop", store.Current.Routes.Messages
            .Values.Single().Kind);
    }

    [TestMethod]
    public async Task CompletionWithoutRecipientsClearsNotificationClaim()
    {
        await using var fixture = await RetryFixture.CreateAsync(
            chats: [],
            autoRetry: false,
            ready: false);

        await fixture.Coordinator.HandleAsync(Event(
            "completion-local-only",
            RuntimeEventTypes.TurnCompleted,
            "turn-local-completion",
            new { turnId = "turn-local-completion" }));

        var session = fixture.Store.Current.Sessions.Sessions[SessionId];
        Assert.AreEqual(0, fixture.Gateway.Sends.Count);
        Assert.AreEqual(0, fixture.Store.Current.Routes.Messages.Count);
        Assert.IsNull(ExtensionString(session, "lastNotificationStatus"));
        Assert.IsNull(ExtensionString(session, "lastNotificationTurnId"));
        Assert.IsNull(ExtensionString(session, "pendingNotificationKind"));
    }

    [TestMethod]
    public async Task CompletionStateSinkFailureHasNoNotificationSideEffect()
    {
        await using var fixture = await RetryFixture.CreateAsync(
            stateError: new InvalidOperationException("synthetic completion state failure"));

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            fixture.Coordinator.HandleAsync(Event(
                "completion-state-failure",
                RuntimeEventTypes.TurnCompleted,
                "turn-completion-state-failure",
                new { message = "不会发送。" })));

        Assert.AreEqual(0, fixture.Gateway.Sends.Count);
        Assert.AreEqual(0, fixture.Store.Updates);
    }

    [TestMethod]
    public async Task PendingPartialDeliveryRecoversWithStableIdempotencyKeys()
    {
        var gateway = new RecordingFeishuGateway();
        gateway.FailChats.Add("chat-2");
        var store = new RecordingStoreOwner(StoreSnapshot(
            ["chat-1", "chat-2"],
            autoRetry: true));

        await using (var first = await RetryFixture.CreateAsync(
            store: store,
            gateway: gateway,
            ready: false))
        {
            await first.Coordinator.HandleAsync(Failure("turn-recover", "HTTP 503"));
        }

        Assert.AreEqual("pending", ExtensionString(
            store.Current.Sessions.Sessions[SessionId],
            "lastNotificationStatus"));
        var firstKey = gateway.Attempts.Single(attempt =>
            attempt.ChatId == "chat-1").IdempotencyKey;
        gateway.FailChats.Clear();

        await using (var recovered = await RetryFixture.CreateAsync(
            store: store,
            gateway: gateway,
            ready: false))
        {
            Assert.AreEqual("sent", ExtensionString(
                store.Current.Sessions.Sessions[SessionId],
                "lastNotificationStatus"));
        }

        var recoveredKey = gateway.Attempts.Last(attempt =>
            attempt.ChatId == "chat-1").IdempotencyKey;
        Assert.AreEqual(firstKey, recoveredKey);
        Assert.AreEqual(2, store.Current.Routes.Messages.Count);
        Assert.IsTrue(store.Current.Routes.Messages.Values.All(route =>
            route.Kind == "error"));
    }

    [TestMethod]
    public async Task DispatchFailureStopsCycleAndStateSinkFailureHasNoSideEffects()
    {
        await using var dispatchFailure = await RetryFixture.CreateAsync(
            retryDelay: TimeSpan.FromMilliseconds(5));
        dispatchFailure.RuntimeCommands.DispatchError =
            new InvalidOperationException("synthetic dispatch failure");
        await dispatchFailure.Coordinator.HandleAsync(Failure(
            "turn-dispatch-failure",
            "HTTP 502"));

        await WaitUntilAsync(
            () => !dispatchFailure.Coordinator.HasActiveRetry(SessionId) &&
                dispatchFailure.Gateway.Patches.Count >= 2,
            TimeSpan.FromSeconds(2));
        Assert.IsFalse(dispatchFailure.Coordinator.HasActiveRetry(SessionId));
        Assert.IsFalse(CardJson(dispatchFailure.Gateway.Patches.Last().Card).Contains(
            FeishuCardActions.RetryStop,
            StringComparison.Ordinal));

        await using var stateFailure = await RetryFixture.CreateAsync(
            stateError: new InvalidOperationException("synthetic state failure"));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            stateFailure.Coordinator.HandleAsync(Failure(
                "turn-state-failure",
                "HTTP 502")));
        Assert.AreEqual(0, stateFailure.Gateway.Sends.Count);
        Assert.AreEqual(0, stateFailure.RuntimeCommands.Commands.Count);
        Assert.AreEqual(0, stateFailure.Store.Updates);
    }

    private const string SessionId = "session-retry-1";

    private static RuntimeEventEnvelope Failure(
        string turnId,
        string error,
        string? code = null) => Event(
            $"event-{turnId}",
            RuntimeEventTypes.TurnFailed,
            turnId,
            new { turnId, error, code });

    private static RuntimeEventEnvelope Event(
        string eventId,
        string eventType,
        string turnId,
        object payload,
        string? runtime = null,
        string? sessionId = null) => new()
        {
            ProtocolVersion = BridgeProtocolVersion.Current,
            Runtime = runtime ?? RuntimeNames.Codex,
            Session = new RuntimeSessionReference
            {
                ExternalId = sessionId ?? SessionId,
                Cwd = "K:/repo",
            },
            TraceId = $"trace-{eventId}",
            CorrelationId = turnId,
            EventId = eventId,
            EventType = eventType,
            OccurredAt = "2026-08-08T00:00:00.000Z",
            Payload = JsonSerializer.SerializeToElement(payload),
        };

    private static NodeStoreSnapshot StoreSnapshot(
        IReadOnlyList<string> chats,
        bool autoRetry,
        string runtime = RuntimeNames.Codex,
        IReadOnlyDictionary<string, JsonElement>? sessionExtensions = null)
    {
        var users = chats.Select((chatId, index) => new BindingStoreRecord
            {
                OpenId = $"owner-{index}",
                ChatId = chatId,
                ChatType = "p2p",
                BoundAt = "2026-08-08T00:00:00.000Z",
            })
            .ToDictionary(binding => binding.OpenId, StringComparer.Ordinal);
        var extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["alias"] = JsonSerializer.SerializeToElement("retry-test"),
        };
        if (sessionExtensions is not null)
        {
            foreach (var (name, value) in sessionExtensions)
            {
                extensions[name] = value.Clone();
            }
        }
        var session = new SessionStoreRecord
        {
            SessionId = SessionId,
            ShortId = "retry-1",
            ProjectName = "repo",
            Cwd = "K:/repo",
            Runtime = runtime,
            Status = SessionStatuses.Waiting,
            OpenedAt = "2026-08-08T00:00:00.000Z",
            LastSeenAt = "2026-08-08T00:00:00.000Z",
            LastError = null,
            ExtensionData = extensions,
        };
        return new(
            new BindingStoreDocument
            {
                OwnerOpenId = users.Keys.FirstOrDefault(),
                Users = users,
            },
            new SessionStoreDocument
            {
                Sessions = new Dictionary<string, SessionStoreRecord>(StringComparer.Ordinal)
                {
                    [SessionId] = session,
                },
            },
            new RouteStoreDocument(),
            new ApprovalStoreDocument(),
            new SettingsStoreDocument
            {
                AutoRetryErrors = autoRetry,
                RetryMaxAttempts = 3,
                RetryIntervalSeconds = 5,
                RetryJitterSeconds = 0,
            },
            new ControlTokenStoreDocument());
    }

    private static NodeStoreSnapshot WithPendingNotification(
        NodeStoreSnapshot store,
        string turnId,
        string kind,
        string message) => NodeStoreBusinessStateMerger.PatchSessionExtensions(
        store,
        SessionId,
        new Dictionary<string, JsonElement?>
        {
            ["lastNotificationTurnId"] = JsonSerializer.SerializeToElement(turnId),
            ["lastNotificationStatus"] = JsonSerializer.SerializeToElement("pending"),
            ["pendingNotificationKind"] = JsonSerializer.SerializeToElement(kind),
            ["pendingNotificationMessage"] = JsonSerializer.SerializeToElement(message),
        });

    private static string? ExtensionString(ExtensibleStoreObject value, string name) =>
        value.ExtensionData is not null &&
        value.ExtensionData.TryGetValue(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string RequiredJsonString(FeishuCardView card, string name)
    {
        using var document = JsonDocument.Parse(CardJson(card));
        return FindString(document.RootElement, name) ??
            throw new AssertFailedException($"卡片缺少字符串字段 {name}。");
    }

    private static string? FindString(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.NameEquals(name) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
                if (FindString(property.Value, name) is { } nested)
                {
                    return nested;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (FindString(item, name) is { } nested)
                {
                    return nested;
                }
            }
        }
        return null;
    }

    private static string CardJson(FeishuCardView card) => card.Content.ToJsonString(
        new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail("等待条件超时。");
            }
            await Task.Delay(10);
        }
    }

    private sealed class RetryFixture : IAsyncDisposable
    {
        private RetryFixture(
            ActiveRuntimeRetryCoordinator coordinator,
            RecordingStoreOwner store,
            RecordingRuntimeCommandGateway runtimeCommands,
            RecordingFeishuGateway gateway,
            ConcurrentQueue<string> actions,
            RecordingStateSink state,
            RecordingFileTransferCoordinator fileTransfers)
        {
            Coordinator = coordinator;
            Store = store;
            RuntimeCommands = runtimeCommands;
            Gateway = gateway;
            Actions = actions;
            State = state;
            FileTransfers = fileTransfers;
        }

        public ActiveRuntimeRetryCoordinator Coordinator { get; }
        public RecordingStoreOwner Store { get; }
        public RecordingRuntimeCommandGateway RuntimeCommands { get; }
        public RecordingFeishuGateway Gateway { get; }
        public ConcurrentQueue<string> Actions { get; }
        public RecordingStateSink State { get; }
        public RecordingFileTransferCoordinator FileTransfers { get; }

        public static async Task<RetryFixture> CreateAsync(
            RecordingStoreOwner? store = null,
            RecordingFeishuGateway? gateway = null,
            IReadOnlyList<string>? chats = null,
            bool autoRetry = true,
            bool ready = true,
            string runtime = RuntimeNames.Codex,
            TimeSpan? retryDelay = null,
            Exception? stateError = null,
            IBridgeActiveSessionGroupCoordinator? sessionGroups = null,
            ConcurrentQueue<string>? actions = null,
            IBridgeActiveApprovalNotifier? approvalNotifications = null)
        {
            actions ??= new ConcurrentQueue<string>();
            store ??= new RecordingStoreOwner(StoreSnapshot(
                chats ?? ["chat-1"],
                autoRetry,
                runtime));
            gateway ??= new RecordingFeishuGateway();
            gateway.Actions = actions;
            var state = new RecordingStateSink(actions) { Error = stateError };
            var runtimeCommands = new RecordingRuntimeCommandGateway(actions)
            {
                Ready = ready,
            };
            var fileTransfers = new RecordingFileTransferCoordinator();
            var options = new BridgeHostOptions(
                Path.GetTempPath(),
                IPAddress.Loopback,
                0,
                BridgeOwnershipMode.Active,
                "active-runtime-retry-test");
            var coordinator = new ActiveRuntimeRetryCoordinator(
                options,
                state,
                store,
                runtimeCommands,
                gateway,
                new FeishuCardRenderer(),
                retryDelayOverride: retryDelay ?? TimeSpan.FromSeconds(5),
                jitterSelector: _ => 0,
                fileTransfers: fileTransfers,
                sessionGroups: sessionGroups,
                approvalNotifications: approvalNotifications);
            await coordinator.StartAsync(CancellationToken.None);
            return new(
                coordinator,
                store,
                runtimeCommands,
                gateway,
                actions,
                state,
                fileTransfers);
        }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.StopAsync(CancellationToken.None);
            Coordinator.Dispose();
        }
    }

    private sealed class RecordingStateSink(ConcurrentQueue<string> actions) :
        IBridgeActiveRuntimeStateSink
    {
        public Exception? Error { get; set; }
        public bool Completed { get; private set; }
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? PersistenceGate { get; set; }

        public BridgeBusinessStateSnapshot Snapshot =>
            BridgeBusinessStateSnapshot.NotInitialized;

        public async Task HandleAsync(
            RuntimeEventEnvelope runtimeEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            actions.Enqueue($"state:{runtimeEvent.EventType}");
            Entered.TrySetResult();
            if (PersistenceGate is not null)
            {
                await PersistenceGate.Task.WaitAsync(cancellationToken);
            }
            if (Error is not null)
            {
                throw Error;
            }
            Completed = true;
        }
    }

    private sealed class RecordingApprovalNotifier(ConcurrentQueue<string> actions) :
        IBridgeActiveApprovalNotifier
    {
        public Exception? Error { get; set; }

        public Task NotifyPendingAsync(
            string requestId,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            actions.Enqueue($"approval:{requestId}:{sessionId}");
            return Error is null
                ? Task.CompletedTask
                : Task.FromException(Error);
        }

        public Task SynchronizeAsync(
            string requestId,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            actions.Enqueue($"approval-sync:{requestId}:{sessionId}");
            return Error is null
                ? Task.CompletedTask
                : Task.FromException(Error);
        }

        public Task SynchronizeSessionAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            actions.Enqueue($"approval-session-sync:{sessionId}");
            return Error is null
                ? Task.CompletedTask
                : Task.FromException(Error);
        }
    }

    private sealed class RecordingSessionGroupCoordinator :
        IBridgeActiveSessionGroupCoordinator
    {
        private readonly ConcurrentQueue<string>? actions;
        private readonly IReadOnlyList<string> chats;

        public RecordingSessionGroupCoordinator(
            ConcurrentQueue<string>? actions = null,
            IReadOnlyList<string>? chats = null)
        {
            this.actions = actions;
            this.chats = chats ?? ["chat-group"];
        }

        public List<string> NotificationRequests { get; } = [];

        public ValueTask<SessionStoreRecord?> EnsureAsync(
            string sessionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<SessionStoreRecord?>(null);

        public ValueTask<BridgeSessionGroupRetryResult> RetryAsync(
            string sessionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new BridgeSessionGroupRetryResult(
                false,
                false,
                null,
                null,
                "recording coordinator"));

        public ValueTask<IReadOnlyList<string>> NotificationChatsAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            NotificationRequests.Add(sessionId);
            return ValueTask.FromResult(chats);
        }

        public void ScheduleEnsure(string sessionId) =>
            actions?.Enqueue($"group:{sessionId}");
    }

    private sealed class RecordingStoreOwner(NodeStoreSnapshot current) :
        IBridgeProductionStoreOwner
    {
        private readonly object sync = new();
        private NodeStoreSnapshot current = current;

        public int Updates { get; private set; }

        public NodeStoreSnapshot Current
        {
            get
            {
                lock (sync)
                {
                    return current;
                }
            }
        }

        public BridgeProductionStoreSnapshot Snapshot => new(
            BridgeProductionStoreState.Open,
            Current,
            6);

        public ValueTask OpenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<NodeStoreSnapshot> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Current);
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask UpdateAsync(
            Func<NodeStoreSnapshot, NodeStoreSnapshot> update,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                current = update(current);
                Updates++;
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class RecordingRuntimeCommandGateway(
        ConcurrentQueue<string> actions) : IBridgeRuntimeCommandGateway
    {
        private readonly object sync = new();
        private readonly List<RuntimeCommandEnvelope> commands = [];

        public bool Ready { get; set; }
        public Exception? DispatchError { get; set; }
        public TaskCompletionSource<RuntimeCommandEnvelope> Dispatched { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<RuntimeCommandEnvelope> Commands
        {
            get
            {
                lock (sync)
                {
                    return commands.ToArray();
                }
            }
        }

        public bool IsReady(string runtime, RuntimeSession session) => Ready;

        public Task DispatchAsync(
            RuntimeCommandEnvelope command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            actions.Enqueue("dispatch");
            lock (sync)
            {
                commands.Add(command);
            }
            Dispatched.TrySetResult(command);
            return DispatchError is null
                ? Task.CompletedTask
                : Task.FromException(DispatchError);
        }
    }

    private sealed class RecordingFeishuGateway : IFeishuGateway
    {
        private readonly object sync = new();
        private readonly Dictionary<string, string> messageIdsByKey =
            new(StringComparer.Ordinal);
        private readonly List<SendAttempt> attempts = [];
        private readonly List<SentCard> sends = [];
        private readonly List<(string MessageId, FeishuCardView Card)> patches = [];

        public ConcurrentQueue<string>? Actions { get; set; }
        public Action? BeforeSend { get; set; }
        public HashSet<string> FailChats { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<SendAttempt> Attempts
        {
            get
            {
                lock (sync)
                {
                    return attempts.ToArray();
                }
            }
        }

        public IReadOnlyList<SentCard> Sends
        {
            get
            {
                lock (sync)
                {
                    return sends.ToArray();
                }
            }
        }

        public IReadOnlyList<(string MessageId, FeishuCardView Card)> Patches
        {
            get
            {
                lock (sync)
                {
                    return patches.ToArray();
                }
            }
        }

        public Task<string> SendCardAsync(
            string chatId,
            FeishuCardView card,
            string? idempotencyKey = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = idempotencyKey ?? throw new AssertFailedException("错误通知缺少幂等键。");
            BeforeSend?.Invoke();
            Actions?.Enqueue($"send:{chatId}");
            lock (sync)
            {
                attempts.Add(new(chatId, key));
                if (FailChats.Contains(chatId))
                {
                    throw new InvalidOperationException("synthetic send failure");
                }
                if (!messageIdsByKey.TryGetValue(key, out var messageId))
                {
                    messageId = $"message-{messageIdsByKey.Count + 1}";
                    messageIdsByKey[key] = messageId;
                }
                if (!sends.Any(send => send.MessageId == messageId))
                {
                    sends.Add(new(messageId, chatId, key, card));
                }
                return Task.FromResult(messageId);
            }
        }

        public Task PatchCardAsync(
            string messageId,
            FeishuCardView card,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                patches.Add((messageId, card));
            }
            return Task.CompletedTask;
        }

        public Task<string> SendTextAsync(
            string chatId,
            string text,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("重试协调器不应发送文本。");

        public Task<string> ReplyTextAsync(
            string messageId,
            string text,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("重试协调器不应回复文本。");

        public Task<FeishuSessionGroup> CreateSessionGroupAsync(
            string ownerOpenId,
            string name,
            string description,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("重试协调器不应创建群聊。");

        public Task UpdateSessionGroupNameAsync(
            string chatId,
            string name,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("重试协调器不应更新群聊。");

        public Task DeleteSessionGroupAsync(
            string chatId,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("重试协调器不应删除群聊。");

        public Task<long> DownloadMessageResourceAsync(
            string messageId,
            string fileKey,
            string resourceType,
            string destinationPath,
            long maxBytes,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("重试协调器不应下载资源。");

        public Task<string> SendLocalFileAsync(
            string chatId,
            string filePath,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("重试协调器不应发送文件。");
    }

    private sealed record SendAttempt(string ChatId, string IdempotencyKey);

    private sealed record SentCard(
        string MessageId,
        string ChatId,
        string IdempotencyKey,
        FeishuCardView Card);
}
