using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActiveFeishuPromptCoordinatorTests
{
    [TestMethod]
    public async Task PrivateAliasQueueDispatchesPromptAndPersistsAcknowledgementRoute()
    {
        var target = Session(
            "codex-session-aaa12345",
            RuntimeNames.Codex,
            SessionStatuses.Running,
            alias: "alpha",
            managedTerminalId: "terminal-1");
        var other = Session(
            "claude-session-bbb67890",
            RuntimeNames.ClaudeCode,
            SessionStatuses.Waiting,
            alias: "beta",
            managedTerminalId: "terminal-2");
        var fixture = Fixture.Create(
            [target, other],
            readySessionIds: Set(target.SessionId));

        await fixture.Coordinator.HandleAsync(
            Intent("排队 @alpha 修复测试"),
            fixture.Store.Current);

        var command = fixture.RuntimeCommands.Commands.Single();
        Assert.AreEqual(RuntimeCommandTypes.PromptSend, command.CommandType);
        Assert.AreEqual(target.SessionId, command.Session!.ExternalId);
        Assert.AreEqual("修复测试", command.Payload.GetProperty("prompt").GetString());
        Assert.AreEqual("queue", command.Payload.GetProperty("mode").GetString());
        Assert.IsTrue(BridgeProtocolValidator.Validate(command).IsValid);
        CollectionAssert.AreEqual(
            new[] { target.SessionId },
            fixture.RuntimeRetries.ManualSessions.ToArray());
        Assert.AreEqual("Codex 已接收。", fixture.Gateway.Replies.Single().Text);
        var route = fixture.Store.Current.Routes.Messages["reply-1"];
        Assert.AreEqual(target.SessionId, route.SessionId);
        Assert.AreEqual("resume_ack", route.Kind);
        Assert.AreEqual(1, fixture.Store.Updates);
    }

    [TestMethod]
    public async Task GroupPrefixCannotRedirectAndOfflineManagedSessionResumes()
    {
        var group = Session(
            "codex-session-group1234",
            RuntimeNames.Codex,
            SessionStatuses.Waiting,
            alias: "group",
            feishuChatId: "group-chat",
            managedByAssistant: true);
        var other = Session(
            "codex-session-other9999",
            RuntimeNames.Codex,
            SessionStatuses.Waiting,
            alias: "other",
            managedTerminalId: "terminal-other");
        var fixture = Fixture.Create([group, other]);

        await fixture.Coordinator.HandleAsync(
            Intent("#9999 继续处理", chatId: "group-chat", chatType: "group"),
            fixture.Store.Current);

        var command = fixture.RuntimeCommands.Commands.Single();
        Assert.AreEqual(RuntimeCommandTypes.SessionResume, command.CommandType);
        Assert.AreEqual(group.SessionId, command.Session!.ExternalId);
        Assert.AreEqual("继续处理", command.Payload.GetProperty("prompt").GetString());
        StringAssert.Contains(fixture.Gateway.Replies.Single().Text, "自动恢复");
        Assert.AreEqual(
            group.SessionId,
            fixture.Store.Current.Routes.Messages["reply-1"].SessionId);
    }

    [TestMethod]
    public async Task StaleClientProcessIdInManagedGroupTriggersRecovery()
    {
        var group = Session(
            "codex-session-stale123",
            RuntimeNames.Codex,
            SessionStatuses.Waiting,
            feishuChatId: "group-chat",
            managedByAssistant: true,
            clientProcessId: int.MaxValue,
            clientProcessStartedAt: "2026-08-11T00:00:00.000Z");
        var fixture = Fixture.Create([group]);

        await fixture.Coordinator.HandleAsync(
            Intent("继续处理", chatId: "group-chat", chatType: "group"),
            fixture.Store.Current);

        var command = fixture.RuntimeCommands.Commands.Single();
        Assert.AreEqual(RuntimeCommandTypes.SessionResume, command.CommandType);
        Assert.AreEqual(group.SessionId, command.Session!.ExternalId);
        StringAssert.Contains(fixture.Gateway.Replies.Single().Text, "自动恢复");
    }

    [TestMethod]
    public async Task OpenCodeQueueRequestUsesSteerAndQuotedRouteSelectsSession()
    {
        var codex = Session(
            "codex-session-11112222",
            RuntimeNames.Codex,
            SessionStatuses.Waiting,
            managedTerminalId: "terminal-1");
        var openCode = Session(
            "opencode-session-33334444",
            RuntimeNames.OpenCode,
            SessionStatuses.Running);
        var route = Route("notice-1", openCode.SessionId, "resume_ack");
        var fixture = Fixture.Create(
            [codex, openCode],
            routes: [route],
            readySessionIds: Set(openCode.SessionId));

        await fixture.Coordinator.HandleAsync(
            Intent(
                "排队 继续处理",
                parameters: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["parentMessageId"] = route.MessageId,
                }),
            fixture.Store.Current);

        var command = fixture.RuntimeCommands.Commands.Single();
        Assert.AreEqual(openCode.SessionId, command.Session!.ExternalId);
        Assert.AreEqual("steer", command.Payload.GetProperty("mode").GetString());
        Assert.AreEqual("OpenCode 已接收。", fixture.Gateway.Replies.Single().Text);
    }

    [TestMethod]
    public async Task AmbiguousPrivateAndUnboundGroupTargetsFailClosed()
    {
        var first = Session(
            "codex-session-aaaa7777",
            RuntimeNames.Codex,
            SessionStatuses.Waiting,
            alias: "same",
            managedTerminalId: "terminal-1");
        var second = Session(
            "codex-session-bbbb7777",
            RuntimeNames.Codex,
            SessionStatuses.Waiting,
            alias: "same",
            managedTerminalId: "terminal-2");
        var fixture = Fixture.Create(
            [first, second],
            readySessionIds: Set(first.SessionId, second.SessionId));

        await fixture.Coordinator.HandleAsync(
            Intent("@same 执行"),
            fixture.Store.Current);
        await fixture.Coordinator.HandleAsync(
            Intent("#7777 执行", messageId: "message-2"),
            fixture.Store.Current);
        await fixture.Coordinator.HandleAsync(
            Intent("执行", messageId: "message-3"),
            fixture.Store.Current);
        await fixture.Coordinator.HandleAsync(
            Intent(
                "执行",
                chatId: "unbound-group",
                chatType: "group",
                messageId: "message-4"),
            fixture.Store.Current);

        Assert.AreEqual(0, fixture.RuntimeCommands.Commands.Count);
        Assert.AreEqual(4, fixture.Gateway.Replies.Count);
        StringAssert.Contains(fixture.Gateway.Replies[0].Text, "不是唯一别名");
        StringAssert.Contains(fixture.Gateway.Replies[1].Text, "匹配到多个会话");
        StringAssert.Contains(fixture.Gateway.Replies[2].Text, "有多个活跃会话");
        StringAssert.Contains(fixture.Gateway.Replies[3].Text, "当前群未绑定会话");
    }

    [TestMethod]
    public async Task AttachmentsAndFileReturnDispatchWhileInteractiveQuotesRemainClosed()
    {
        var target = Session(
            "codex-session-aaa12345",
            RuntimeNames.Codex,
            SessionStatuses.Waiting,
            alias: "alpha",
            managedTerminalId: "terminal-1");
        var route = Route(
            "approval-message",
            target.SessionId,
            "activity",
            "approval-1");
        var fixture = Fixture.Create(
            [target],
            routes: [route],
            readySessionIds: Set(target.SessionId));

        await fixture.Coordinator.HandleAsync(
            Intent("", attachments: [new("file", "file-1", "data.txt")]),
            fixture.Store.Current);
        await fixture.Coordinator.HandleAsync(
            Intent(
                "@alpha 发文件 生成报告",
                messageId: "message-2"),
            fixture.Store.Current);
        await fixture.Coordinator.HandleAsync(
            Intent(
                "批准",
                messageId: "message-3",
                parameters: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["parentMessageId"] = route.MessageId,
                }),
            fixture.Store.Current);

        Assert.AreEqual(1, fixture.RuntimeCommands.Commands.Count);
        StringAssert.Contains(
            fixture.RuntimeCommands.Commands.Single().Payload.GetProperty("prompt").GetString()!,
            "K:\\uploads\\message-1-1-data.txt");
        StringAssert.Contains(
            fixture.RuntimeCommands.Commands.Single().Payload.GetProperty("prompt").GetString()!,
            "BRIDGE_SEND_FILE");
        Assert.AreEqual(1, fixture.FileTransfers.Downloads.Count);
        Assert.IsTrue(fixture.FileTransfers.Dispatches.Single().FileReturn);
        Assert.AreEqual(3, fixture.Gateway.Replies.Count);
        StringAssert.Contains(fixture.Gateway.Replies[0].Text, "已安全保存 1 个附件");
        StringAssert.Contains(fixture.Gateway.Replies[2].Text, "审批或问答交互尚未迁移");
    }

    [TestMethod]
    public async Task PendingApprovalAndInputBlockBeforeRuntimeDispatch()
    {
        var target = Session(
            "codex-session-aaa12345",
            RuntimeNames.Codex,
            SessionStatuses.Running,
            managedTerminalId: "terminal-1");
        var observedAt = DateTimeOffset.Parse("2026-08-07T00:00:00.000Z");
        var approval = new ApprovalState(
            "approval-1",
            target.SessionId,
            ApprovalStatuses.Pending,
            observedAt,
            observedAt.AddMinutes(5),
            []);
        var input = new InputRequestState(
            "input-1",
            target.SessionId,
            InputRequestStatuses.Pending,
            observedAt,
            observedAt.AddMinutes(5),
            [new("question-1", false, true, [])],
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
        var approvalFixture = Fixture.Create(
            [target],
            approvals: [approval],
            readySessionIds: Set(target.SessionId));
        var inputFixture = Fixture.Create(
            [target],
            inputs: [input],
            readySessionIds: Set(target.SessionId));

        await approvalFixture.Coordinator.HandleAsync(
            Intent("继续"),
            approvalFixture.Store.Current);
        await inputFixture.Coordinator.HandleAsync(
            Intent("继续"),
            inputFixture.Store.Current);

        Assert.AreEqual(0, approvalFixture.RuntimeCommands.Commands.Count);
        Assert.AreEqual(0, inputFixture.RuntimeCommands.Commands.Count);
        Assert.AreEqual(0, approvalFixture.RuntimeRetries.ManualSessions.Count);
        Assert.AreEqual(0, inputFixture.RuntimeRetries.ManualSessions.Count);
        StringAssert.Contains(
            approvalFixture.Gateway.Replies.Single().Text,
            "请先处理待审批操作");
        StringAssert.Contains(
            inputFixture.Gateway.Replies.Single().Text,
            "请先回答待补充问题");
    }

    [TestMethod]
    public async Task ExternalAndUnavailablePrivateSessionsCannotBeDrivenOrResumed()
    {
        var external = Session(
            "codex-session-external1",
            RuntimeNames.Codex,
            SessionStatuses.Waiting,
            managedByAssistant: false);
        var unavailable = Session(
            "claude-session-managed1",
            RuntimeNames.ClaudeCode,
            SessionStatuses.Waiting,
            managedTerminalId: "terminal-1");
        var externalFixture = Fixture.Create(
            [external],
            readySessionIds: Set(external.SessionId));
        var unavailableFixture = Fixture.Create([unavailable]);

        await externalFixture.Coordinator.HandleAsync(
            Intent("继续"),
            externalFixture.Store.Current);
        await unavailableFixture.Coordinator.HandleAsync(
            Intent("继续"),
            unavailableFixture.Store.Current);

        Assert.AreEqual(0, externalFixture.RuntimeCommands.Commands.Count);
        Assert.AreEqual(0, unavailableFixture.RuntimeCommands.Commands.Count);
        StringAssert.Contains(
            externalFixture.Gateway.Replies.Single().Text,
            "不是由 AI CLI 飞书助手打开");
        StringAssert.Contains(
            unavailableFixture.Gateway.Replies.Single().Text,
            "窗口尚未就绪");
    }

    [TestMethod]
    public async Task EndedGroupSessionIsNotSubmittedForRecovery()
    {
        var ended = Session(
            "codex-session-ended001",
            RuntimeNames.Codex,
            SessionStatuses.Ended,
            feishuChatId: "group-chat",
            managedTerminalId: "terminal-old");
        var fixture = Fixture.Create([ended]);

        await fixture.Coordinator.HandleAsync(
            Intent("继续", chatId: "group-chat", chatType: "group"),
            fixture.Store.Current);

        Assert.AreEqual(0, fixture.RuntimeCommands.Commands.Count);
        StringAssert.Contains(fixture.Gateway.Replies.Single().Text, "对应窗口已关闭");
    }

    [TestMethod]
    public async Task SendFallbackMessageIdIsPersistedAsAcknowledgementRoute()
    {
        var target = Session(
            "codex-session-aaa12345",
            RuntimeNames.Codex,
            SessionStatuses.Waiting,
            managedTerminalId: "terminal-1");
        var fixture = Fixture.Create(
            [target],
            readySessionIds: Set(target.SessionId));
        fixture.Gateway.ReplyFailuresRemaining = 1;

        await fixture.Coordinator.HandleAsync(
            Intent("继续"),
            fixture.Store.Current);

        Assert.AreEqual(0, fixture.Gateway.Replies.Count);
        Assert.AreEqual("Codex 已接收。", fixture.Gateway.SentTexts.Single().Text);
        Assert.AreEqual(
            target.SessionId,
            fixture.Store.Current.Routes.Messages["sent-1"].SessionId);
    }

    private static FeishuIntent Intent(
        string text,
        string chatId = "chat-1",
        string chatType = "p2p",
        string messageId = "message-1",
        IReadOnlyDictionary<string, string>? parameters = null,
        IReadOnlyList<FeishuAttachment>? attachments = null) => new(
            $"event-{messageId}",
            FeishuIntentTypes.MessagePrompt,
            "owner-1",
            chatId,
            messageId,
            chatType,
            $"trace-{messageId}",
            text,
            parameters,
            attachments);

    private static SessionStoreRecord Session(
        string sessionId,
        string runtime,
        string status,
        string? alias = null,
        string? feishuChatId = null,
        string? managedTerminalId = null,
        bool managedByAssistant = true,
        int? clientProcessId = null,
        string? clientProcessStartedAt = null)
    {
        var extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["managedByAssistant"] = JsonSerializer.SerializeToElement(
                managedByAssistant),
        };
        AddExtension(extensions, "alias", alias);
        AddExtension(extensions, "feishuChatId", feishuChatId);
        AddExtension(extensions, "managedTerminalId", managedTerminalId);
        if (clientProcessId is not null)
        {
            extensions["clientProcessId"] = JsonSerializer.SerializeToElement(
                clientProcessId.Value);
        }
        AddExtension(extensions, "clientProcessStartedAt", clientProcessStartedAt);
        return new()
        {
            SessionId = sessionId,
            ShortId = sessionId[^Math.Min(8, sessionId.Length)..],
            Cwd = $"K:\\workspace\\{sessionId}",
            ProjectName = sessionId,
            Status = status,
            Runtime = runtime,
            OpenedAt = "2026-08-07T00:00:00.000Z",
            LastSeenAt = "2026-08-07T00:01:00.000Z",
            EndedAt = status == SessionStatuses.Ended
                ? "2026-08-07T00:02:00.000Z"
                : null,
            ExtensionData = extensions,
        };
    }

    private static void AddExtension(
        Dictionary<string, JsonElement> extensions,
        string name,
        string? value)
    {
        if (value is not null)
        {
            extensions[name] = JsonSerializer.SerializeToElement(value);
        }
    }

    private static MessageRouteStoreRecord Route(
        string messageId,
        string sessionId,
        string kind,
        string? requestId = null) => new()
        {
            MessageId = messageId,
            SessionId = sessionId,
            ChatId = "chat-1",
            Kind = kind,
            CreatedAt = "2026-08-07T00:00:00.000Z",
            RequestId = requestId,
        };

    private static IReadOnlySet<string> Set(params string[] values) =>
        new HashSet<string>(values, StringComparer.Ordinal);

    private sealed record Fixture(
        ActiveFeishuPromptCoordinator Coordinator,
        RecordingStoreOwner Store,
        RecordingRuntimeCommandGateway RuntimeCommands,
        RecordingRuntimeRetryCoordinator RuntimeRetries,
        RecordingFeishuGateway Gateway,
        RecordingFileTransferCoordinator FileTransfers)
    {
        public static Fixture Create(
            IReadOnlyList<SessionStoreRecord> sessions,
            IReadOnlyList<MessageRouteStoreRecord>? routes = null,
            IReadOnlyList<ApprovalState>? approvals = null,
            IReadOnlyList<InputRequestState>? inputs = null,
            IReadOnlySet<string>? readySessionIds = null)
        {
            var sessionDocument = new SessionStoreDocument
            {
                Sessions = sessions.ToDictionary(
                    session => session.SessionId,
                    StringComparer.Ordinal),
            };
            var routeDocument = new RouteStoreDocument
            {
                Messages = (routes ?? []).ToDictionary(
                    route => route.MessageId,
                    StringComparer.Ordinal),
            };
            var store = new RecordingStoreOwner(new(
                new BindingStoreDocument(),
                sessionDocument,
                routeDocument,
                new ApprovalStoreDocument(),
                new SettingsStoreDocument(),
                new ControlTokenStoreDocument()));
            var business = new RecordingBusinessStateOwner(BusinessSnapshot(
                sessions,
                approvals ?? [],
                inputs ?? []));
            var runtimeCommands = new RecordingRuntimeCommandGateway(
                readySessionIds ?? new HashSet<string>(StringComparer.Ordinal));
            var runtimeRetries = new RecordingRuntimeRetryCoordinator();
            var gateway = new RecordingFeishuGateway();
            var fileTransfers = new RecordingFileTransferCoordinator();
            return new(
                new(store, business, runtimeCommands, runtimeRetries, gateway, fileTransfers),
                store,
                runtimeCommands,
                runtimeRetries,
                gateway,
                fileTransfers);
        }
    }

    private static BridgeBusinessStateSnapshot BusinessSnapshot(
        IReadOnlyList<SessionStoreRecord> sessions,
        IReadOnlyList<ApprovalState> approvals,
        IReadOnlyList<InputRequestState> inputs)
    {
        var observedAt = DateTimeOffset.Parse("2026-08-07T00:00:00.000Z");
        return new(
            true,
            "production",
            1,
            0,
            new SessionDirectoryState(sessions.ToDictionary(
                session => session.SessionId,
                session => new SessionState(
                    session.SessionId,
                    session.Runtime ?? RuntimeNames.Codex,
                    session.Cwd,
                    session.Status,
                    observedAt,
                    observedAt,
                    session.Status == SessionStatuses.Ended ? observedAt : null),
                StringComparer.Ordinal)),
            new ApprovalRegistryState(
                approvals.ToDictionary(
                    approval => approval.RequestId,
                    StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal)),
            new InputRegistryState(inputs.ToDictionary(
                input => input.RequestId,
                StringComparer.Ordinal)));
    }

    private sealed class RecordingStoreOwner(BridgeStoreSnapshot current) :
        IBridgeProductionStoreOwner
    {
        public BridgeStoreSnapshot Current { get; private set; } = current;
        public int Updates { get; private set; }

        public BridgeProductionStoreSnapshot Snapshot => new(
            BridgeProductionStoreState.Open,
            Current,
            6);

        public ValueTask OpenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<BridgeStoreSnapshot> ReadAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Current);

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask UpdateAsync(
            Func<BridgeStoreSnapshot, BridgeStoreSnapshot> update,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Current = update(Current);
            Updates++;
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class RecordingBusinessStateOwner(
        BridgeBusinessStateSnapshot snapshot) : IBridgePersistentBusinessStateOwner
    {
        public BridgeBusinessStateSnapshot Snapshot { get; } = snapshot;
    }

    private sealed class RecordingRuntimeCommandGateway(
        IReadOnlySet<string> readySessionIds) : IBridgeRuntimeCommandGateway
    {
        public List<RuntimeCommandEnvelope> Commands { get; } = [];

        public bool IsReady(string runtime, RuntimeSession session) =>
            readySessionIds.Contains(session.ExternalId);

        public Task DispatchAsync(
            RuntimeCommandEnvelope command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRuntimeRetryCoordinator :
        IBridgeActiveRuntimeRetryCoordinator
    {
        public List<string> ManualSessions { get; } = [];

        public bool HasActiveRetry(string sessionId) => false;

        public ValueTask BeginManualTurnAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ManualSessions.Add(sessionId);
            return ValueTask.CompletedTask;
        }

        public Task<BridgeRetryStopResult> StopAsync(
            string sessionId,
            string cycleId,
            string messageId,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("提示协调器不应直接停止自动重试。");
    }

    private sealed class RecordingFeishuGateway : IFeishuGateway
    {
        public List<(string MessageId, string Text)> Replies { get; } = [];
        public List<(string ChatId, string Text)> SentTexts { get; } = [];
        public int ReplyFailuresRemaining { get; set; }

        public Task<string> SendTextAsync(
            string chatId,
            string text,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SentTexts.Add((chatId, text));
            return Task.FromResult($"sent-{SentTexts.Count}");
        }

        public Task<string> ReplyTextAsync(
            string messageId,
            string text,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReplyFailuresRemaining > 0)
            {
                ReplyFailuresRemaining--;
                throw new InvalidOperationException("synthetic reply failure");
            }
            Replies.Add((messageId, text));
            return Task.FromResult($"reply-{Replies.Count}");
        }

        public Task<string> SendCardAsync(
            string chatId,
            FeishuCardView card,
            string? idempotencyKey = null,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("提示协调器不应发送卡片。");

        public Task PatchCardAsync(
            string messageId,
            FeishuCardView card,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("提示协调器不应更新卡片。");

        public Task<FeishuSessionGroup> CreateSessionGroupAsync(
            string ownerOpenId,
            string name,
            string description,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("提示协调器不应创建群聊。");

        public Task UpdateSessionGroupNameAsync(
            string chatId,
            string name,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("提示协调器不应更新群聊。");

        public Task DeleteSessionGroupAsync(
            string chatId,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("提示协调器不应删除群聊。");

        public Task<long> DownloadMessageResourceAsync(
            string messageId,
            string fileKey,
            string resourceType,
            string destinationPath,
            long maxBytes,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("提示协调器不应下载附件。");

        public Task<string> SendLocalFileAsync(
            string chatId,
            string filePath,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("提示协调器不应发送文件。");
    }
}
