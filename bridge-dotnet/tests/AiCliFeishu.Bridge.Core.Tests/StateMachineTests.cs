using AiCliFeishu.Bridge.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishu.Bridge.Core.Tests;

[TestClass]
public sealed class StateMachineTests
{
    private static readonly DateTimeOffset Origin =
        DateTimeOffset.Parse("2026-08-06T00:00:00Z");

    [TestMethod]
    public void SessionLifecycleReopensAnEndedSessionWithoutMutatingPriorState()
    {
        var original = SessionStateMachine.Register(
            SessionDirectoryState.Empty,
            new SessionState(
                "session-1", "codex", "K:\\project", SessionStatuses.Starting,
                Origin, Origin));
        var ended = SessionStateMachine.Transition(
            original, "session-1", SessionStatuses.Ended, Origin.AddMinutes(1));
        var reopened = SessionStateMachine.Transition(
            ended, "session-1", SessionStatuses.Starting, Origin.AddMinutes(2));

        Assert.AreEqual(SessionStatuses.Starting, original.Sessions["session-1"].Status);
        Assert.AreEqual(SessionStatuses.Ended, ended.Sessions["session-1"].Status);
        Assert.AreEqual(Origin.AddMinutes(2), reopened.Sessions["session-1"].OpenedAt);
        Assert.IsNull(reopened.Sessions["session-1"].EndedAt);
        Assert.ThrowsException<InvalidOperationException>(() =>
            SessionStateMachine.Transition(
                original, "session-1", SessionStatuses.LocalApproval, Origin.AddMinutes(1)));
    }

    [TestMethod]
    public void ApprovalClaimMakesLocalAndFeishuResolutionIdempotent()
    {
        var state = ApprovalStateMachine.Create(
            ApprovalRegistryState.Empty,
            new ApprovalState(
                "approval-1", "session-1", ApprovalStatuses.Pending,
                Origin, Origin.AddMinutes(10), []));
        var claimed = ApprovalStateMachine.Claim(state, "approval-1");
        var duplicateClaim = ApprovalStateMachine.Claim(claimed.State, "approval-1");
        var resolved = ApprovalStateMachine.ResolveClaimed(
            claimed.State, "approval-1", ApprovalResolutions.Allow, Origin.AddMinutes(1));
        var feishuDuplicate = ApprovalStateMachine.ResolveExternally(
            resolved.State, "approval-1", ApprovalResolutions.Deny, Origin.AddMinutes(2));

        Assert.IsTrue(claimed.Value);
        Assert.IsFalse(duplicateClaim.Value);
        Assert.IsTrue(resolved.Value);
        Assert.IsFalse(feishuDuplicate.Value);
        Assert.AreEqual(
            ApprovalResolutions.Allow,
            resolved.State.Requests["approval-1"].Resolution);
        Assert.IsFalse(resolved.State.Claims.Contains("approval-1"));
    }

    [TestMethod]
    public void ApprovalRecoveryReturnsOriginalStateWhenNothingIsPending()
    {
        var pending = ApprovalStateMachine.Create(
            ApprovalRegistryState.Empty,
            new ApprovalState(
                "approval-1", "session-1", ApprovalStatuses.Pending,
                Origin, Origin.AddMinutes(10), []));
        var resolved = ApprovalStateMachine.ResolveExternally(
            pending,
            "approval-1",
            ApprovalResolutions.Allow,
            Origin.AddMinutes(1)).State;

        var recovered = ApprovalStateMachine.RecoverPending(
            resolved,
            Origin.AddMinutes(2));

        Assert.AreSame(resolved, recovered.State);
        Assert.AreEqual(0, recovered.Value);
    }

    [TestMethod]
    public void ApprovalRecoveryOrphansEveryPendingRequestAndClearsClaims()
    {
        var first = ApprovalStateMachine.Create(
            ApprovalRegistryState.Empty,
            new ApprovalState(
                "approval-1", "session-1", ApprovalStatuses.Pending,
                Origin, Origin.AddMinutes(10), []));
        var state = ApprovalStateMachine.Create(
            first,
            new ApprovalState(
                "approval-2", "session-2", ApprovalStatuses.Pending,
                Origin.AddMinutes(1), Origin.AddMinutes(10), []));
        state = ApprovalStateMachine.Claim(state, "approval-1").State;

        var recovered = ApprovalStateMachine.RecoverPending(
            state,
            Origin.AddMinutes(2));

        Assert.AreEqual(2, recovered.Value);
        Assert.AreEqual(0, recovered.State.Claims.Count);
        foreach (var approval in recovered.State.Requests.Values)
        {
            Assert.AreEqual(ApprovalStatuses.Orphaned, approval.Status);
            Assert.AreEqual(ApprovalResolutions.Local, approval.Resolution);
            Assert.AreEqual(Origin.AddMinutes(2), approval.ResolvedAt);
        }
        Assert.AreEqual(ApprovalStatuses.Pending, state.Requests["approval-1"].Status);
        Assert.IsTrue(state.Claims.Contains("approval-1"));
    }

    [TestMethod]
    public void ApprovalRecoveryNeverResolvesBeforeCreationTime()
    {
        var createdAt = Origin.AddMinutes(5);
        var state = ApprovalStateMachine.Create(
            ApprovalRegistryState.Empty,
            new ApprovalState(
                "approval-1", "session-1", ApprovalStatuses.Pending,
                createdAt, createdAt.AddMinutes(10), []));

        var recovered = ApprovalStateMachine.RecoverPending(state, Origin);

        Assert.AreEqual(createdAt, recovered.State.Requests["approval-1"].ResolvedAt);
    }

    [TestMethod]
    public void InputAnswersMustCoverAllQuestionsAndCannotResolveTwice()
    {
        var state = InputStateMachine.Create(
            InputRegistryState.Empty,
            new InputRequestState(
                "input-1", "session-1", InputRequestStatuses.Pending,
                Origin, Origin.AddMinutes(10),
                [
                    new InputQuestionState("runtime", false, false, ["codex", "opencode"]),
                    new InputQuestionState("flags", true, true, ["fast"]),
                ],
                new Dictionary<string, IReadOnlyList<string>>()));
        var incomplete = new Dictionary<string, IReadOnlyList<string>>
        {
            ["runtime"] = ["codex"],
        };
        Assert.ThrowsException<ArgumentException>(() =>
            InputStateMachine.Answer(state, "input-1", incomplete, Origin.AddMinutes(1)));

        var answered = InputStateMachine.Answer(
            state,
            "input-1",
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["runtime"] = ["codex"],
                ["flags"] = ["fast", "custom"],
            },
            Origin.AddMinutes(1));
        var duplicate = InputStateMachine.ResolveExternally(
            answered.State, "input-1", Origin.AddMinutes(2));

        Assert.IsTrue(answered.Value);
        Assert.IsFalse(duplicate.Value);
        Assert.AreEqual(InputRequestStatuses.Resolved, answered.State.Requests["input-1"].Status);
    }

    [TestMethod]
    public void MessageClaimsAreIdempotentAndRetentionKeepsNewestEntries()
    {
        var state = MessageRouteRegistryState.Empty;
        for (var index = 0; index < 4; index++)
        {
            state = MessageRouteStateMachine.AddRoute(
                state,
                new MessageRouteState(
                    $"message-{index}", "session-1", "chat-1", "activity",
                    Origin.AddMinutes(index)));
        }
        var first = MessageRouteStateMachine.ClaimInbound(state, "inbound-1", Origin);
        var duplicate = MessageRouteStateMachine.ClaimInbound(
            first.State, "inbound-1", Origin.AddMinutes(1));
        var pruned = MessageRouteStateMachine.Prune(
            first.State,
            Origin.AddMinutes(4),
            RetentionPolicy.Default with
            {
                RouteRetention = TimeSpan.FromMinutes(10),
                MaxMessageRoutes = 2,
            });

        Assert.IsTrue(first.Value);
        Assert.IsFalse(duplicate.Value);
        CollectionAssert.AreEquivalent(
            new[] { "message-2", "message-3" },
            pruned.Messages.Keys.ToArray());
    }

    [TestMethod]
    public void RetryAndLaunchTasksRequireClaimBeforeCompletion()
    {
        var retries = RetryStateMachine.Schedule(
            RetryRegistryState.Empty,
            new RetryTaskState(
                "retry-1", "session-1", 1, 3, RetryStatuses.Pending,
                Origin.AddMinutes(1), Origin));
        Assert.IsFalse(RetryStateMachine.ClaimDue(retries, "retry-1", Origin).Value);
        var retryClaim = RetryStateMachine.ClaimDue(
            retries, "retry-1", Origin.AddMinutes(1));
        var retryDone = RetryStateMachine.Complete(
            retryClaim.State, "retry-1", false, Origin.AddMinutes(2));
        Assert.AreEqual(RetryStatuses.Failed, retryDone.Tasks["retry-1"].Status);

        var launches = LaunchStateMachine.Queue(
            LaunchRegistryState.Empty,
            new LaunchRequestState(
                "launch-1", "resume", "opencode", "K:\\project",
                LaunchStatuses.Pending, Origin, Origin.AddMinutes(2), "session-1"));
        var launchClaim = LaunchStateMachine.Claim(
            launches, "launch-1", Origin.AddMinutes(1));
        var launchDone = LaunchStateMachine.Complete(
            launchClaim.State, "launch-1", true, Origin.AddMinutes(1));

        Assert.IsTrue(retryClaim.Value);
        Assert.IsTrue(launchClaim.Value);
        Assert.AreEqual(LaunchStatuses.Launched, launchDone.Requests["launch-1"].Status);
        Assert.ThrowsException<InvalidOperationException>(() =>
            LaunchStateMachine.Complete(
                launches, "launch-1", true, Origin.AddMinutes(1)));
    }

    [TestMethod]
    public void ApprovalRetentionNeverDropsPendingByCountLimit()
    {
        var requests = new Dictionary<string, ApprovalState>(StringComparer.Ordinal)
        {
            ["pending"] = new(
                "pending", "session-1", ApprovalStatuses.Pending,
                Origin, Origin.AddDays(10), []),
            ["old"] = new(
                "old", "session-1", ApprovalStatuses.Resolved,
                Origin, Origin.AddMinutes(1), [], ApprovalResolutions.Allow, Origin.AddMinutes(1)),
            ["new"] = new(
                "new", "session-1", ApprovalStatuses.Resolved,
                Origin.AddMinutes(2), Origin.AddMinutes(3), [],
                ApprovalResolutions.Deny, Origin.AddMinutes(3)),
        };
        var pruned = ApprovalRetention.Prune(
            new ApprovalRegistryState(requests, new HashSet<string>()),
            Origin.AddMinutes(4),
            RetentionPolicy.Default with
            {
                ApprovalRetention = TimeSpan.FromDays(1),
                MaxCompletedApprovals = 1,
            });

        Assert.IsTrue(pruned.Requests.ContainsKey("pending"));
        Assert.IsTrue(pruned.Requests.ContainsKey("new"));
        Assert.IsFalse(pruned.Requests.ContainsKey("old"));
    }
}
