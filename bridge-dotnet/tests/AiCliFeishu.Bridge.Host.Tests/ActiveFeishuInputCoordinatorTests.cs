using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActiveFeishuInputCoordinatorTests
{
    private static readonly DateTimeOffset Origin =
        DateTimeOffset.Parse("2026-08-07T00:00:00.000Z");

    [TestMethod]
    public async Task AnswersAreRecordedPerQuestionAndDispatchedOnceWhenComplete()
    {
        var fixture = Fixture.Create(RuntimeNames.Codex);

        var first = await fixture.Coordinator.HandleAsync(
            Intent(FeishuIntentTypes.InputAnswer, "q1", "safe", "card-q1"),
            fixture.Store);

        Assert.AreEqual("success", first.ToastType);
        Assert.IsNull(first.Card);
        Assert.IsNotNull(first.AfterAcknowledged);
        Assert.AreEqual(0, fixture.Gateway.Patches.Count);
        await first.AfterAcknowledged(CancellationToken.None);
        Assert.IsTrue(fixture.Gateway.Patches.Any(item =>
            item.MessageId == "card-q1" &&
            CardText(item.Card).Contains("已记录回答", StringComparison.Ordinal)));

        var toggled = await fixture.Coordinator.HandleAsync(
            Intent(
                FeishuIntentTypes.InputToggle,
                "q2",
                "code",
                "card-q2",
                selectionKey: "chat-1"),
            fixture.Store);

        Assert.AreEqual("success", toggled.ToastType);
        Assert.IsNull(toggled.Card);
        Assert.IsNotNull(toggled.AfterAcknowledged);
        await toggled.AfterAcknowledged(CancellationToken.None);
        Assert.IsTrue(fixture.Gateway.Patches.Any(item =>
            item.MessageId == "card-q2" &&
            CardText(item.Card).Contains("✓ code", StringComparison.Ordinal)));

        var submitted = await fixture.Coordinator.HandleAsync(
            Intent(
                FeishuIntentTypes.InputSubmit,
                "q2",
                messageId: "card-q2",
                selectionKey: "chat-1"),
            fixture.Store);

        Assert.AreEqual("success", submitted.ToastType);
        Assert.IsNull(submitted.Card);
        Assert.IsNotNull(submitted.AfterAcknowledged);
        await submitted.AfterAcknowledged(CancellationToken.None);
        Assert.IsTrue(fixture.Gateway.Patches.Any(item =>
            CardText(item.Card).Contains("补充信息已提交", StringComparison.Ordinal) &&
            !item.Card.Content.ToJsonString().Contains(
                FeishuCardActions.InputAnswer,
                StringComparison.Ordinal)));
        Assert.AreEqual(1, fixture.Runtime.Commands.Count);
        var command = fixture.Runtime.Commands.Single();
        Assert.AreEqual("feishu-input-input-1", command.CommandId);
        Assert.AreEqual(RuntimeCommandTypes.InputResolve, command.CommandType);
        CollectionAssert.AreEqual(
            new[] { "safe" },
            command.Payload.GetProperty("answers").GetProperty("q1")
                .EnumerateArray().Select(item => item.GetString()).ToArray());
        CollectionAssert.AreEqual(
            new[] { "code" },
            command.Payload.GetProperty("answers").GetProperty("q2")
                .EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.AreEqual(
            InputRequestStatuses.Resolved,
            fixture.State.Snapshot.Inputs.Requests["input-1"].Status);
        Assert.AreEqual(
            SessionStatuses.Running,
            fixture.State.Snapshot.Sessions.Sessions["session-1"].Status);
        CollectionAssert.IsSubsetOf(
            new[] { "card-q1", "card-q2" },
            fixture.Gateway.Patches.Select(item => item.MessageId).ToArray());
    }

    [TestMethod]
    public async Task DispatchFailureResetsAnswersAndKeepsMultiSelectionForRetry()
    {
        var fixture = Fixture.Create(RuntimeNames.ClaudeCode);
        var first = await fixture.Coordinator.HandleAsync(
            Intent(FeishuIntentTypes.InputAnswer, "q1", "safe", "card-q1"),
            fixture.Store);
        Assert.IsNotNull(first.AfterAcknowledged);
        await first.AfterAcknowledged(CancellationToken.None);
        var toggled = await fixture.Coordinator.HandleAsync(
            Intent(
                FeishuIntentTypes.InputToggle,
                "q2",
                "docs",
                "card-q2",
                selectionKey: "chat-1"),
            fixture.Store);
        Assert.IsNotNull(toggled.AfterAcknowledged);
        await toggled.AfterAcknowledged(CancellationToken.None);
        fixture.Runtime.Error = new InvalidOperationException("synthetic failure");

        var failed = await fixture.Coordinator.HandleAsync(
            Intent(
                FeishuIntentTypes.InputSubmit,
                "q2",
                messageId: "card-q2",
                selectionKey: "chat-1"),
            fixture.Store);

        Assert.AreEqual("warning", failed.ToastType);
        Assert.IsNull(failed.Card);
        Assert.IsNotNull(failed.AfterAcknowledged);
        await failed.AfterAcknowledged(CancellationToken.None);
        Assert.AreEqual(0, fixture.State.Snapshot.Inputs.Requests["input-1"].Answers.Count);
        Assert.AreEqual(
            InputRequestStatuses.Pending,
            fixture.State.Snapshot.Inputs.Requests["input-1"].Status);
        Assert.IsTrue(fixture.Gateway.Patches.Any(item =>
            item.MessageId == "card-q2" &&
            CardText(item.Card).Contains("✓ docs", StringComparison.Ordinal)));

        fixture.Runtime.Error = null;
        var retriedFirst = await fixture.Coordinator.HandleAsync(
            Intent(
                FeishuIntentTypes.InputAnswer,
                "q1",
                "safe",
                "card-q1",
                eventId: "event-retry-q1"),
            fixture.Store);
        Assert.IsNotNull(retriedFirst.AfterAcknowledged);
        await retriedFirst.AfterAcknowledged(CancellationToken.None);
        var retried = await fixture.Coordinator.HandleAsync(
            Intent(
                FeishuIntentTypes.InputSubmit,
                "q2",
                messageId: "card-q2",
                selectionKey: "chat-1",
                eventId: "event-retry-q2"),
            fixture.Store);

        Assert.AreEqual("success", retried.ToastType);
        Assert.IsNull(retried.Card);
        Assert.IsNotNull(retried.AfterAcknowledged);
        await retried.AfterAcknowledged(CancellationToken.None);
        Assert.AreEqual(2, fixture.Runtime.Commands.Count);
        Assert.IsTrue(fixture.Runtime.Commands.All(command =>
            command.CommandId == "feishu-input-input-1"));
    }

    [TestMethod]
    public async Task DeferKeepsOpenCodePendingWithoutSendingRuntimeAnswers()
    {
        var fixture = Fixture.Create(RuntimeNames.OpenCode, ready: false);

        var result = await fixture.Coordinator.HandleAsync(
            Intent(
                FeishuIntentTypes.InputDeferToLocal,
                "q1",
                messageId: "card-q1"),
            fixture.Store);

        Assert.AreEqual("success", result.ToastType);
        Assert.IsNull(result.Card);
        Assert.IsNotNull(result.AfterAcknowledged);
        Assert.AreEqual(0, fixture.Gateway.Patches.Count);
        await result.AfterAcknowledged(CancellationToken.None);
        Assert.IsTrue(fixture.Gateway.Patches.Any(item =>
            CardText(item.Card).Contains("补充信息已处理", StringComparison.Ordinal)));
        Assert.AreEqual(0, fixture.Runtime.Commands.Count);
        Assert.AreEqual(
            InputRequestStatuses.Local,
            fixture.State.Snapshot.Inputs.Requests["input-1"].Status);
        Assert.AreEqual(
            SessionStatuses.PendingInput,
            fixture.State.Snapshot.Sessions.Sessions["session-1"].Status);
        Assert.IsTrue(fixture.Gateway.Patches.All(item =>
            !CardText(item.Card).Contains("input_answer", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ManagedRuntimeDeferReleasesHookWithoutSendingRuntimeAnswers()
    {
        var fixture = Fixture.Create(RuntimeNames.Codex);

        var result = await fixture.Coordinator.HandleAsync(
            Intent(
                FeishuIntentTypes.InputDeferToLocal,
                "q1",
                messageId: "card-q1"),
            fixture.Store);

        Assert.AreEqual("success", result.ToastType);
        Assert.IsNull(result.Card);
        Assert.IsNotNull(result.AfterAcknowledged);
        Assert.AreEqual(0, fixture.Gateway.Patches.Count);
        await result.AfterAcknowledged(CancellationToken.None);
        Assert.IsTrue(fixture.Gateway.Patches.Any(item =>
            CardText(item.Card).Contains("补充信息已处理", StringComparison.Ordinal)));
        Assert.AreEqual(0, fixture.Runtime.Commands.Count);
        Assert.AreEqual(1, fixture.ManagedHooks.Deferred.Count);
        Assert.AreEqual(
            (RuntimeNames.Codex, "session-1", "input-1"),
            fixture.ManagedHooks.Deferred.Single());
        Assert.AreEqual(
            InputRequestStatuses.Local,
            fixture.State.Snapshot.Inputs.Requests["input-1"].Status);
        Assert.AreEqual(
            SessionStatuses.Waiting,
            fixture.State.Snapshot.Sessions.Sessions["session-1"].Status);
    }

    [TestMethod]
    public async Task ManagedRuntimeDeferCommitsStateAfterHookReleaseWhenRequestIsCancelled()
    {
        var fixture = Fixture.Create(RuntimeNames.Codex);
        using var cancellation = new CancellationTokenSource();
        fixture.ManagedHooks.DeferHandler = (_, _, _, _) =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };

        var result = await fixture.Coordinator.HandleAsync(
            Intent(
                FeishuIntentTypes.InputDeferToLocal,
                "q1",
                messageId: "card-q1"),
            fixture.Store,
            cancellation.Token);

        Assert.AreEqual("success", result.ToastType);
        Assert.IsNull(result.Card);
        Assert.IsNotNull(result.AfterAcknowledged);
        await result.AfterAcknowledged(CancellationToken.None);
        Assert.AreEqual(1, fixture.ManagedHooks.Deferred.Count);
        Assert.AreEqual(0, fixture.Runtime.Commands.Count);
        Assert.AreEqual(
            InputRequestStatuses.Local,
            fixture.State.Snapshot.Inputs.Requests["input-1"].Status);
        Assert.AreEqual(
            SessionStatuses.Waiting,
            fixture.State.Snapshot.Sessions.Sessions["session-1"].Status);
    }

    [TestMethod]
    public async Task SelectionScopeTamperingAndInvalidAnswersFailClosed()
    {
        var fixture = Fixture.Create(RuntimeNames.Codex);

        var scope = await fixture.Coordinator.HandleAsync(
            Intent(
                FeishuIntentTypes.InputToggle,
                "q2",
                "code",
                "card-q2",
                selectionKey: "other-chat"),
            fixture.Store);
        var invalid = await fixture.Coordinator.HandleAsync(
            Intent(FeishuIntentTypes.InputAnswer, "q1", "other", "card-q1"),
            fixture.Store);
        var wrongSession = await fixture.Coordinator.HandleAsync(
            Intent(
                FeishuIntentTypes.InputAnswer,
                "q1",
                "safe",
                "card-q1",
                sessionId: "other-session"),
            fixture.Store);

        Assert.AreEqual("warning", scope.ToastType);
        Assert.AreEqual("error", invalid.ToastType);
        Assert.AreEqual("warning", wrongSession.ToastType);
        Assert.AreEqual(0, fixture.Runtime.Commands.Count);
        Assert.AreEqual(0, fixture.State.Snapshot.Inputs.Requests["input-1"].Answers.Count);
    }

    [TestMethod]
    public async Task QuotedSingleQuestionReplyUsesCompatibleInputRoute()
    {
        var fixture = Fixture.Create(
            RuntimeNames.Codex,
            questions: [Question("q1", multiple: false)]);
        fixture.Store.Routes.Messages["question-card"] = new()
        {
            MessageId = "question-card",
            SessionId = "session-1",
            ChatId = "chat-1",
            Kind = "input",
            RequestId = "input-1",
            CreatedAt = Origin.ToString("O"),
        };
        var intent = new FeishuIntent(
            "event-reply",
            FeishuIntentTypes.MessagePrompt,
            "owner-1",
            "chat-1",
            "reply-message",
            "p2p",
            "trace-reply",
            "2",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["parentMessageId"] = "question-card",
            });

        var handled = await fixture.Coordinator.TryHandleQuotedReplyAsync(
            intent,
            fixture.Store);

        Assert.IsTrue(handled);
        Assert.AreEqual(1, fixture.Runtime.Commands.Count);
        CollectionAssert.AreEqual(
            new[] { "fast" },
            fixture.Runtime.Commands[0].Payload.GetProperty("answers")
                .GetProperty("q1").EnumerateArray()
                .Select(item => item.GetString()).ToArray());
        Assert.IsTrue(fixture.Gateway.Replies.Any(item =>
            item.MessageId == "reply-message" &&
            item.Text.Contains("已把答案交给", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task QuotedSingleQuestionReplyCanResolveTheThreadRootRoute()
    {
        var fixture = Fixture.Create(
            RuntimeNames.Codex,
            questions: [Question("q1", multiple: false)]);
        fixture.Store.Routes.Messages["question-card"] = new()
        {
            MessageId = "question-card",
            SessionId = "session-1",
            ChatId = "chat-1",
            Kind = "input",
            RequestId = "input-1",
            CreatedAt = Origin.ToString("O"),
        };
        var intent = new FeishuIntent(
            "event-root-reply",
            FeishuIntentTypes.MessagePrompt,
            "owner-1",
            "chat-1",
            "reply-message",
            "p2p",
            "trace-root-reply",
            "fast",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rootMessageId"] = "question-card",
            });

        var handled = await fixture.Coordinator.TryHandleQuotedReplyAsync(
            intent,
            fixture.Store);

        Assert.IsTrue(handled);
        Assert.AreEqual(1, fixture.Runtime.Commands.Count);
        Assert.IsTrue(fixture.Gateway.Replies.Any(item =>
            item.MessageId == "reply-message" &&
            item.Text.Contains("已把答案交给", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ExternalCompletionDuringDispatchUsesClaimedFeishuAnswers()
    {
        var fixture = Fixture.Create(
            RuntimeNames.OpenCode,
            questions: [Question("q1", multiple: false)]);
        fixture.Runtime.Handler = (_, _) =>
        {
            fixture.State.ResolveExternally("input-1");
            return Task.CompletedTask;
        };

        var result = await fixture.Coordinator.HandleAsync(
            Intent(FeishuIntentTypes.InputAnswer, "q1", "safe", "card-q1"),
            fixture.Store);

        Assert.AreEqual("success", result.ToastType);
        var input = fixture.State.Snapshot.Inputs.Requests["input-1"];
        Assert.AreEqual(InputRequestStatuses.Resolved, input.Status);
        CollectionAssert.AreEqual(new[] { "safe" }, input.Answers["q1"].ToArray());
    }

    [TestMethod]
    public async Task ConcurrentFinalClicksDispatchOnlyOneAnswer()
    {
        var fixture = Fixture.Create(
            RuntimeNames.Codex,
            questions: [Question("q1", multiple: false)]);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.Handler = async (_, cancellationToken) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
        };

        var firstTask = fixture.Coordinator.HandleAsync(
            Intent(FeishuIntentTypes.InputAnswer, "q1", "safe", "card-q1"),
            fixture.Store);
        await entered.Task;
        var second = await fixture.Coordinator.HandleAsync(
            Intent(
                FeishuIntentTypes.InputAnswer,
                "q1",
                "fast",
                "card-q1",
                eventId: "event-2"),
            fixture.Store);
        release.SetResult();
        var first = await firstTask;

        Assert.AreEqual("success", first.ToastType);
        Assert.AreEqual("warning", second.ToastType);
        Assert.AreEqual(1, fixture.Runtime.Commands.Count);
    }

    private static InputQuestionState Question(string id, bool multiple) => new(
        id,
        multiple,
        false,
        id == "q1" ? ["safe", "fast"] : ["code", "docs"],
        id == "q1" ? "模式" : "范围",
        id == "q1" ? "请选择模式" : "请选择范围");

    private static FeishuIntent Intent(
        string intentType,
        string? questionId = null,
        string? answer = null,
        string messageId = "card-1",
        string? selectionKey = null,
        string sessionId = "session-1",
        string eventId = "event-1")
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["requestId"] = "input-1",
            ["sessionId"] = sessionId,
        };
        if (questionId is not null)
        {
            parameters["questionId"] = questionId;
        }
        if (answer is not null)
        {
            parameters["answer"] = answer;
        }
        if (selectionKey is not null)
        {
            parameters["selectionKey"] = selectionKey;
        }
        return new(
            eventId,
            intentType,
            "owner-1",
            "chat-1",
            messageId,
            "card",
            $"trace-{eventId}",
            Parameters: parameters);
    }

    private static string CardText(FeishuCardView card) => string.Join(
        '\n',
        Descendants(card.Content)
            .OfType<System.Text.Json.Nodes.JsonValue>()
            .Select(value => value.TryGetValue<string>(out var text) ? text : null)
            .Where(text => text is not null));

    private static IEnumerable<System.Text.Json.Nodes.JsonNode> Descendants(
        System.Text.Json.Nodes.JsonNode node)
    {
        yield return node;
        if (node is System.Text.Json.Nodes.JsonObject owner)
        {
            foreach (var child in owner.Select(item => item.Value).Where(item => item is not null))
            {
                foreach (var descendant in Descendants(child!))
                {
                    yield return descendant;
                }
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (var child in array.Where(item => item is not null))
            {
                foreach (var descendant in Descendants(child!))
                {
                    yield return descendant;
                }
            }
        }
    }

    private sealed record Fixture(
        ActiveFeishuInputCoordinator Coordinator,
        RecordingInputStateOwner State,
        RecordingRuntimeCommandGateway Runtime,
        RecordingFeishuGateway Gateway,
        RecordingManagedHookResponseSink ManagedHooks,
        BridgeStoreSnapshot Store)
    {
        public static Fixture Create(
            string runtime,
            bool ready = true,
            IReadOnlyList<InputQuestionState>? questions = null)
        {
            questions ??= [Question("q1", false), Question("q2", true)];
            var store = StoreSnapshot(runtime);
            var state = new RecordingInputStateOwner(store, questions);
            var commands = new RecordingRuntimeCommandGateway { Ready = ready };
            var gateway = new RecordingFeishuGateway();
            var renderer = new FeishuCardRenderer();
            var interactions = new FeishuInteractionCoordinator(
                gateway,
                renderer,
                new InMemoryFeishuCardPatchLedger());
            var managedHooks = new RecordingManagedHookResponseSink();
            return new(
                new(
                    state,
                    commands,
                    interactions,
                    renderer,
                    gateway,
                    managedHooks,
                    new FixedTimeProvider(Origin.AddMinutes(1))),
                state,
                commands,
                gateway,
                managedHooks,
                store);
        }
    }

    private static BridgeStoreSnapshot StoreSnapshot(string runtime)
    {
        var session = new SessionStoreRecord
        {
            SessionId = "session-1",
            ShortId = "12345678",
            Cwd = "K:\\workspace\\project",
            ProjectName = "project",
            Runtime = runtime,
            Status = SessionStatuses.PendingInput,
            OpenedAt = Origin.ToString("O"),
            LastSeenAt = Origin.AddMinutes(1).ToString("O"),
        };
        return new(
            new BindingStoreDocument(),
            new SessionStoreDocument
            {
                Sessions = new Dictionary<string, SessionStoreRecord>(StringComparer.Ordinal)
                {
                    [session.SessionId] = session,
                },
            },
            new RouteStoreDocument(),
            new ApprovalStoreDocument(),
            new SettingsStoreDocument(),
            new ControlTokenStoreDocument());
    }

    private sealed class RecordingInputStateOwner : IBridgeActiveInputStateOwner
    {
        private readonly object sync = new();
        private readonly HashSet<string> claims = new(StringComparer.Ordinal);
        private BridgeBusinessStateSnapshot snapshot;

        public RecordingInputStateOwner(
            BridgeStoreSnapshot store,
            IReadOnlyList<InputQuestionState> questions)
        {
            var session = store.Sessions.Sessions["session-1"];
            var sessions = new SessionDirectoryState(
                new Dictionary<string, SessionState>(StringComparer.Ordinal)
                {
                    [session.SessionId] = new(
                        session.SessionId,
                        session.Runtime!,
                        session.Cwd,
                        SessionStatuses.PendingInput,
                        Origin,
                        Origin.AddMinutes(1)),
                });
            var input = new InputRequestState(
                "input-1",
                session.SessionId,
                InputRequestStatuses.Pending,
                Origin,
                Origin.AddMinutes(10),
                questions,
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
            snapshot = new(
                true,
                "production",
                1,
                0,
                sessions,
                ApprovalRegistryState.Empty,
                new InputRegistryState(
                    new Dictionary<string, InputRequestState>(StringComparer.Ordinal)
                    {
                        [input.RequestId] = input,
                    }));
        }

        public BridgeBusinessStateSnapshot Snapshot
        {
            get
            {
                lock (sync)
                {
                    return snapshot;
                }
            }
        }

        public ValueTask<BridgeInputAnswerProgress?> TryRecordInputAnswerAsync(
            string requestId,
            string sessionId,
            string questionId,
            IReadOnlyList<string> answers,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (claims.Contains(requestId) || !TryPending(requestId, sessionId, out var input, out var session))
                {
                    return ValueTask.FromResult<BridgeInputAnswerProgress?>(null);
                }
                var recorded = InputStateMachine.RecordAnswer(
                    snapshot.Inputs,
                    requestId,
                    questionId,
                    answers);
                if (!recorded.Value)
                {
                    return ValueTask.FromResult<BridgeInputAnswerProgress?>(null);
                }
                input = recorded.State.Requests[requestId];
                var complete = InputStateMachine.HasCompleteAnswers(input);
                if (complete)
                {
                    claims.Add(requestId);
                }
                snapshot = snapshot with
                {
                    Revision = snapshot.Revision + 1,
                    Inputs = recorded.State,
                };
                return ValueTask.FromResult<BridgeInputAnswerProgress?>(new(
                    input,
                    session,
                    complete));
            }
        }

        public ValueTask<BridgeInputClaim?> TryClaimInputAsync(
            string requestId,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                return TryPending(requestId, sessionId, out var input, out var session) &&
                    claims.Add(requestId)
                        ? ValueTask.FromResult<BridgeInputClaim?>(new(input, session))
                        : ValueTask.FromResult<BridgeInputClaim?>(null);
            }
        }

        public ValueTask<BridgeInputClaim?> ResolveClaimedInputAsync(
            string requestId,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (TryCompleted(requestId, sessionId, out var completed))
                {
                    claims.Remove(requestId);
                    return ValueTask.FromResult<BridgeInputClaim?>(completed);
                }
                if (!claims.Contains(requestId) ||
                    !TryPending(requestId, sessionId, out var input, out _))
                {
                    return ValueTask.FromResult<BridgeInputClaim?>(null);
                }
                var resolved = InputStateMachine.Answer(
                    snapshot.Inputs,
                    requestId,
                    input.Answers,
                    Origin.AddMinutes(2));
                Complete(requestId, sessionId, resolved.State, SessionStatuses.Running);
                return ValueTask.FromResult<BridgeInputClaim?>(new(
                    snapshot.Inputs.Requests[requestId],
                    snapshot.Sessions.Sessions[sessionId]));
            }
        }

        public ValueTask<BridgeInputClaim?> DeferClaimedInputAsync(
            string requestId,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (!claims.Contains(requestId) ||
                    !TryPending(requestId, sessionId, out _, out var session))
                {
                    return ValueTask.FromResult<BridgeInputClaim?>(null);
                }
                var local = InputStateMachine.ResolveExternally(
                    snapshot.Inputs,
                    requestId,
                    Origin.AddMinutes(2));
                Complete(
                    requestId,
                    sessionId,
                    local.State,
                    session.Runtime == RuntimeNames.OpenCode
                        ? SessionStatuses.PendingInput
                        : SessionStatuses.Waiting);
                return ValueTask.FromResult<BridgeInputClaim?>(new(
                    snapshot.Inputs.Requests[requestId],
                    snapshot.Sessions.Sessions[sessionId]));
            }
        }

        public ValueTask ReleaseInputClaimAsync(
            string requestId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                claims.Remove(requestId);
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<BridgeInputClaim?> ResetClaimedInputAsync(
            string requestId,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (TryCompleted(requestId, sessionId, out var completed))
                {
                    claims.Remove(requestId);
                    return ValueTask.FromResult<BridgeInputClaim?>(completed);
                }
                if (!claims.Remove(requestId) ||
                    !TryPending(requestId, sessionId, out var input, out var session))
                {
                    return ValueTask.FromResult<BridgeInputClaim?>(null);
                }
                var reset = InputStateMachine.ClearAnswers(snapshot.Inputs, requestId);
                snapshot = snapshot with
                {
                    Revision = snapshot.Revision + 1,
                    Inputs = reset.State,
                };
                return ValueTask.FromResult<BridgeInputClaim?>(new(
                    reset.State.Requests[requestId],
                    session));
            }
        }

        public void ResolveExternally(string requestId)
        {
            lock (sync)
            {
                var input = snapshot.Inputs.Requests[requestId];
                var resolved = claims.Contains(requestId) &&
                    InputStateMachine.HasCompleteAnswers(input)
                        ? InputStateMachine.Answer(
                            snapshot.Inputs,
                            requestId,
                            input.Answers,
                            Origin.AddMinutes(2))
                        : InputStateMachine.ResolveExternally(
                            snapshot.Inputs,
                            requestId,
                            Origin.AddMinutes(2));
                Complete(requestId, input.SessionId, resolved.State, SessionStatuses.Running);
            }
        }

        private bool TryPending(
            string requestId,
            string sessionId,
            out InputRequestState input,
            out SessionState session)
        {
            if (snapshot.Inputs.Requests.TryGetValue(requestId, out input!) &&
                input.Status == InputRequestStatuses.Pending &&
                input.SessionId == sessionId &&
                snapshot.Sessions.Sessions.TryGetValue(sessionId, out session!))
            {
                return true;
            }
            input = null!;
            session = null!;
            return false;
        }

        private bool TryCompleted(
            string requestId,
            string sessionId,
            out BridgeInputClaim? completed)
        {
            if (snapshot.Inputs.Requests.TryGetValue(requestId, out var input) &&
                input.Status == InputRequestStatuses.Resolved &&
                input.SessionId == sessionId &&
                snapshot.Sessions.Sessions.TryGetValue(sessionId, out var session))
            {
                completed = new(input, session);
                return true;
            }
            completed = null;
            return false;
        }

        private void Complete(
            string requestId,
            string sessionId,
            InputRegistryState inputs,
            string sessionStatus)
        {
            claims.Remove(requestId);
            snapshot = snapshot with
            {
                Revision = snapshot.Revision + 1,
                Inputs = inputs,
                Sessions = SessionStateMachine.Transition(
                    snapshot.Sessions,
                    sessionId,
                    sessionStatus,
                    Origin.AddMinutes(2)),
            };
        }
    }

    private sealed class RecordingRuntimeCommandGateway : IBridgeRuntimeCommandGateway
    {
        public List<RuntimeCommandEnvelope> Commands { get; } = [];
        public bool Ready { get; set; }
        public Exception? Error { get; set; }
        public Func<RuntimeCommandEnvelope, CancellationToken, Task>? Handler { get; set; }

        public bool IsReady(string runtime, RuntimeSession session) => Ready;

        public async Task DispatchAsync(
            RuntimeCommandEnvelope command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            if (Error is not null)
            {
                throw Error;
            }
            if (Handler is not null)
            {
                await Handler(command, cancellationToken);
            }
        }
    }

    private sealed class RecordingManagedHookResponseSink : IManagedHookResponseSink
    {
        public List<(string Runtime, string SessionId, string RequestId)> Deferred { get; } = [];
        public Func<string, string, string, CancellationToken, Task>? DeferHandler { get; set; }

        public bool IsReady(string runtime, string sessionExternalId) => true;

        public Task ResolveApprovalAsync(
            RuntimeCommandContext context,
            string runtime,
            string sessionExternalId,
            string requestId,
            string decision,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResolveInputAsync(
            RuntimeCommandContext context,
            string runtime,
            string sessionExternalId,
            string requestId,
            IReadOnlyDictionary<string, IReadOnlyList<string>> answers,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeferInputToLocalAsync(
            string runtime,
            string sessionExternalId,
            string requestId,
            CancellationToken cancellationToken = default)
        {
            Deferred.Add((runtime, sessionExternalId, requestId));
            return DeferHandler?.Invoke(
                runtime,
                sessionExternalId,
                requestId,
                cancellationToken) ?? Task.CompletedTask;
        }
    }

    private sealed class RecordingFeishuGateway : IFeishuGateway
    {
        public List<(string MessageId, FeishuCardView Card)> Patches { get; } = [];
        public List<(string MessageId, string Text)> Replies { get; } = [];
        public List<(string ChatId, string Text)> SentTexts { get; } = [];

        public Task PatchCardAsync(
            string messageId,
            FeishuCardView card,
            CancellationToken cancellationToken = default)
        {
            Patches.Add((messageId, card));
            return Task.CompletedTask;
        }

        public Task<string> SendTextAsync(
            string chatId,
            string text,
            CancellationToken cancellationToken = default)
        {
            SentTexts.Add((chatId, text));
            return Task.FromResult($"sent-{SentTexts.Count}");
        }

        public Task<string> ReplyTextAsync(
            string messageId,
            string text,
            CancellationToken cancellationToken = default)
        {
            Replies.Add((messageId, text));
            return Task.FromResult($"reply-{Replies.Count}");
        }

        public Task<string> SendCardAsync(
            string chatId,
            FeishuCardView card,
            string? idempotencyKey = null,
            CancellationToken cancellationToken = default) => Unexpected<string>();

        public Task<FeishuSessionGroup> CreateSessionGroupAsync(
            string ownerOpenId,
            string name,
            string description,
            CancellationToken cancellationToken = default) => Unexpected<FeishuSessionGroup>();

        public Task UpdateSessionGroupNameAsync(
            string chatId,
            string name,
            CancellationToken cancellationToken = default) => Unexpected();

        public Task DeleteSessionGroupAsync(
            string chatId,
            CancellationToken cancellationToken = default) => Unexpected();

        public Task<long> DownloadMessageResourceAsync(
            string messageId,
            string fileKey,
            string resourceType,
            string destinationPath,
            long maxBytes,
            CancellationToken cancellationToken = default) => Unexpected<long>();

        public Task<string> SendLocalFileAsync(
            string chatId,
            string filePath,
            CancellationToken cancellationToken = default) => Unexpected<string>();

        private static Task Unexpected() =>
            Task.FromException(new AssertFailedException("问答协调器不应调用这个飞书端口。"));

        private static Task<T> Unexpected<T>() =>
            Task.FromException<T>(new AssertFailedException(
                "问答协调器不应调用这个飞书端口。"));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
