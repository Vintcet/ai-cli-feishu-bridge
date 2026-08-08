using System.Net;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Encodings.Web;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActiveFeishuIntentHandlerTests
{
    [TestMethod]
    public async Task BoundOwnerCanOpenMenuAndReplaceItWithRuntimeSelection()
    {
        var fixture = Fixture.Create(bound: true);

        var menuResult = await fixture.Handler.HandleAsync(
            Intent(FeishuIntentTypes.CommandMenu));
        var newResult = await fixture.Handler.HandleAsync(
            Intent(FeishuIntentTypes.CommandNew, chatType: "card"));

        Assert.IsNull(menuResult);
        Assert.AreEqual(1, fixture.Gateway.Cards.Count);
        Assert.AreEqual("feishu-intent:event-1", fixture.Gateway.Cards[0].IdempotencyKey);
        StringAssert.Contains(
            CardJson(fixture.Gateway.Cards[0].Card),
            "AI CLI 飞书助手命令");
        Assert.IsNotNull(newResult?.Card);
        Assert.AreEqual("success", newResult.ToastType);
        StringAssert.Contains(CardJson(newResult.Card), "新建 AI CLI 会话");
        Assert.AreEqual(1, fixture.Gateway.Cards.Count);
        Assert.AreEqual(2, fixture.Store.Reads);
    }

    [TestMethod]
    public async Task RuntimeSelectionCanBeCancelledAndCancellationIsFinal()
    {
        var fixture = Fixture.Create(bound: true);
        var selection = await fixture.Handler.HandleAsync(
            RuntimeIntent(FeishuIntentTypes.RuntimeNewSelect, "flow-cancel"));
        var cancelled = await fixture.Handler.HandleAsync(
            RuntimeIntent(FeishuIntentTypes.RuntimeNewCancel, "flow-cancel"));
        var repeated = await fixture.Handler.HandleAsync(
            RuntimeIntent(FeishuIntentTypes.RuntimeNewCancel, "flow-cancel"));
        var submit = await fixture.Handler.HandleAsync(
            RuntimeIntent(
                FeishuIntentTypes.RuntimeNewSubmit,
                "flow-cancel",
                projectName: "cancelled-project"));

        Assert.AreEqual("info", selection?.ToastType);
        StringAssert.Contains(CardJson(selection!.Card!), "project_name");
        Assert.AreEqual("success", cancelled?.ToastType);
        StringAssert.Contains(CardJson(cancelled!.Card!), "已取消新建");
        Assert.AreEqual("info", repeated?.ToastType);
        Assert.AreEqual("warning", submit?.ToastType);
        StringAssert.Contains(submit!.ToastContent, "已经取消");
        Assert.AreEqual(0, fixture.RuntimeCommands.Commands.Count);
    }

    [TestMethod]
    public async Task RuntimeSubmitCreatesProjectAndDispatchesOneStandardLaunch()
    {
        using var directory = new TemporaryDirectory();
        var fixture = Fixture.Create(bound: true, workspaceRoot: directory.Path);

        var result = await fixture.Handler.HandleAsync(
            RuntimeIntent(
                FeishuIntentTypes.RuntimeNewSubmit,
                "flow-submit",
                RuntimeNames.ClaudeCode,
                "新项目"));
        var duplicate = await fixture.Handler.HandleAsync(
            RuntimeIntent(
                FeishuIntentTypes.RuntimeNewSubmit,
                "flow-submit",
                RuntimeNames.ClaudeCode,
                "新项目",
                eventId: "event-2"));

        Assert.AreEqual("success", result?.ToastType);
        StringAssert.Contains(CardJson(result!.Card!), "已提交新建请求");
        Assert.AreEqual("warning", duplicate?.ToastType);
        Assert.AreEqual(1, fixture.RuntimeCommands.Commands.Count);
        var command = fixture.RuntimeCommands.Commands.Single();
        Assert.AreEqual(BridgeProtocolVersion.Current, command.ProtocolVersion);
        Assert.AreEqual(RuntimeNames.ClaudeCode, command.Runtime);
        Assert.AreEqual(RuntimeCommandTypes.SessionLaunch, command.CommandType);
        Assert.AreEqual("trace-1", command.TraceId);
        Assert.AreEqual("flow-submit", command.CorrelationId);
        StringAssert.StartsWith(command.Session!.ExternalId, "launch-");
        Assert.AreEqual(command.Session.Cwd, command.Payload.GetProperty("cwd").GetString());
        Assert.IsFalse(command.Payload.GetProperty("elevated").GetBoolean());
        Assert.IsTrue(Directory.Exists(command.Session.Cwd));
        Assert.IsTrue(BridgeProtocolValidator.Validate(command).IsValid);
    }

    [TestMethod]
    public async Task ConcurrentRuntimeSubmitIsDispatchedOnlyOnce()
    {
        using var directory = new TemporaryDirectory();
        var fixture = Fixture.Create(bound: true, workspaceRoot: directory.Path);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.RuntimeCommands.Handler = async (_, cancellationToken) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
        };

        var firstTask = fixture.Handler.HandleAsync(
            RuntimeIntent(
                FeishuIntentTypes.RuntimeNewSubmit,
                "flow-concurrent",
                projectName: "concurrent-project"));
        await entered.Task;
        var duplicate = await fixture.Handler.HandleAsync(
            RuntimeIntent(
                FeishuIntentTypes.RuntimeNewSubmit,
                "flow-concurrent",
                projectName: "concurrent-project",
                eventId: "event-2"));
        release.SetResult();
        var first = await firstTask;

        Assert.AreEqual("success", first?.ToastType);
        Assert.AreEqual("warning", duplicate?.ToastType);
        Assert.AreEqual(1, fixture.RuntimeCommands.Commands.Count);
    }

    [TestMethod]
    public async Task RuntimeSubmitRejectsInvalidProjectNamesBeforeFilesystemAccess()
    {
        using var directory = new TemporaryDirectory();
        var fixture = Fixture.Create(bound: true, workspaceRoot: directory.Path);
        var missing = await fixture.Handler.HandleAsync(
            RuntimeIntent(
                FeishuIntentTypes.RuntimeNewSubmit,
                "flow-missing"));
        var invalidNames = new[]
        {
            ".",
            "..",
            "CON",
            "con.txt",
            "bad/name",
            "bad:name",
            "bad.",
            "bad ",
            "bad\u0001name",
            new string('a', 81),
        };

        Assert.AreEqual("error", missing?.ToastType);
        Assert.AreEqual("请输入项目名。", missing?.ToastContent);
        for (var index = 0; index < invalidNames.Length; index++)
        {
            var result = await fixture.Handler.HandleAsync(
                RuntimeIntent(
                    FeishuIntentTypes.RuntimeNewSubmit,
                    $"flow-invalid-{index}",
                    projectName: invalidNames[index],
                    eventId: $"event-{index}"));

            Assert.AreEqual("error", result?.ToastType, invalidNames[index]);
            StringAssert.Contains(result!.ToastContent, "项目名不正确");
        }
        Assert.AreEqual(0, fixture.RuntimeCommands.Commands.Count);
        Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(directory.Path).Count());
    }

    [TestMethod]
    public async Task RuntimeSubmitRejectsExistingFileAndLinkedDirectory()
    {
        using var directory = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "occupied"), "data");
        var linkedPath = Path.Combine(directory.Path, "linked");
        CreateDirectoryLink(linkedPath, outside.Path);
        try
        {
            var fixture = Fixture.Create(bound: true, workspaceRoot: directory.Path);

            var fileResult = await fixture.Handler.HandleAsync(
                RuntimeIntent(
                    FeishuIntentTypes.RuntimeNewSubmit,
                    "flow-file",
                    projectName: "occupied"));
            var linkResult = await fixture.Handler.HandleAsync(
                RuntimeIntent(
                    FeishuIntentTypes.RuntimeNewSubmit,
                    "flow-link",
                    projectName: "linked",
                    eventId: "event-2"));

            Assert.AreEqual("error", fileResult?.ToastType);
            StringAssert.Contains(fileResult!.ToastContent, "普通文件夹");
            Assert.AreEqual("error", linkResult?.ToastType);
            StringAssert.Contains(linkResult!.ToastContent, "普通文件夹");
            Assert.AreEqual(0, fixture.RuntimeCommands.Commands.Count);
            Assert.IsTrue(Directory.Exists(outside.Path));
        }
        finally
        {
            Directory.Delete(linkedPath);
        }
    }

    [TestMethod]
    public async Task DispatchFailureRollsBackNewEmptyDirectoryAndAllowsRetry()
    {
        using var directory = new TemporaryDirectory();
        var fixture = Fixture.Create(bound: true, workspaceRoot: directory.Path);
        fixture.RuntimeCommands.Error = new InvalidOperationException("synthetic failure");
        var intent = RuntimeIntent(
            FeishuIntentTypes.RuntimeNewSubmit,
            "flow-retry",
            projectName: "retry-project");

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            fixture.Handler.HandleAsync(intent));

        Assert.IsFalse(Directory.Exists(Path.Combine(directory.Path, "retry-project")));
        fixture.RuntimeCommands.Error = null;
        var retried = await fixture.Handler.HandleAsync(intent with { EventId = "event-2" });

        Assert.AreEqual("success", retried?.ToastType);
        Assert.AreEqual(2, fixture.RuntimeCommands.Commands.Count);
        Assert.IsTrue(Directory.Exists(Path.Combine(directory.Path, "retry-project")));
    }

    [TestMethod]
    public async Task DispatchFailureDoesNotDeleteExistingProjectDirectory()
    {
        using var directory = new TemporaryDirectory();
        var projectPath = Path.Combine(directory.Path, "existing-project");
        Directory.CreateDirectory(projectPath);
        await File.WriteAllTextAsync(Path.Combine(projectPath, "keep.txt"), "keep");
        var fixture = Fixture.Create(bound: true, workspaceRoot: directory.Path);
        fixture.RuntimeCommands.Error = new InvalidOperationException("synthetic failure");

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            fixture.Handler.HandleAsync(
                RuntimeIntent(
                    FeishuIntentTypes.RuntimeNewSubmit,
                    "flow-existing",
                    projectName: "existing-project")));

        Assert.IsTrue(File.Exists(Path.Combine(projectPath, "keep.txt")));
    }

    [TestMethod]
    public async Task ReadOnlyCommandsUseProductionSnapshotAndReplyFallback()
    {
        var fixture = Fixture.Create(bound: true);
        fixture.Gateway.ReplyFailuresRemaining = 1;

        await fixture.Handler.HandleAsync(Intent(FeishuIntentTypes.CommandStatus));
        await fixture.Handler.HandleAsync(Intent(FeishuIntentTypes.CommandWorkspace));
        await fixture.Handler.HandleAsync(Intent(FeishuIntentTypes.CommandSessions));
        await fixture.Handler.HandleAsync(Intent(FeishuIntentTypes.CommandAliases));
        await fixture.Handler.HandleAsync(Intent(FeishuIntentTypes.CommandHelp));

        Assert.AreEqual(1, fixture.Gateway.SentTexts.Count);
        StringAssert.Contains(fixture.Gateway.SentTexts[0].Text, "活跃会话 1 个");
        StringAssert.Contains(fixture.Gateway.SentTexts[0].Text, "待审批 1 个");
        StringAssert.Contains(fixture.Gateway.SentTexts[0].Text, "待补充 1 个");
        StringAssert.Contains(fixture.Gateway.SentTexts[0].Text, "排队 2 条");
        Assert.AreEqual(4, fixture.Gateway.Replies.Count);
        StringAssert.Contains(fixture.Gateway.Replies[0].Text, "K:\\workspace");
        StringAssert.Contains(fixture.Gateway.Replies[1].Text, "alpha");
        StringAssert.Contains(fixture.Gateway.Replies[2].Text, "@alpha");
        StringAssert.Contains(fixture.Gateway.Replies[3].Text, "/新建");
        Assert.AreEqual(5, fixture.Store.Reads);
    }

    [TestMethod]
    public async Task PrivateAliasCommandsSetRenameQueryAndClearThroughStateOwner()
    {
        var fixture = Fixture.Create(bound: true);
        fixture.Store.AllowUpdates = true;

        await fixture.Handler.HandleAsync(AliasIntent("别名 #12345678 主项目"));
        await fixture.Handler.HandleAsync(AliasIntent(
            "别名 @主项目",
            eventId: "event-alias-query"));
        await fixture.Handler.HandleAsync(AliasIntent(
            "别名 @主项目 新名称",
            eventId: "event-alias-rename"));
        await fixture.Handler.HandleAsync(AliasIntent(
            "别名 #12345678 清除",
            eventId: "event-alias-clear"));

        Assert.AreEqual(3, fixture.SessionAliases.Calls.Count);
        Assert.AreEqual("主项目", fixture.SessionAliases.Calls[0].Alias);
        Assert.AreEqual("新名称", fixture.SessionAliases.Calls[1].Alias);
        Assert.IsNull(fixture.SessionAliases.Calls[2].Alias);
        StringAssert.Contains(fixture.Gateway.Replies[0].Text, "@主项目");
        StringAssert.Contains(fixture.Gateway.Replies[1].Text, "别名是 @主项目");
        StringAssert.Contains(fixture.Gateway.Replies[2].Text, "@新名称");
        StringAssert.Contains(fixture.Gateway.Replies[3].Text, "已清除");
        Assert.IsFalse(fixture.Store.Current.Sessions.Sessions["session-12345678"]
            .ExtensionData?.ContainsKey("alias") == true);
        Assert.AreEqual(0, fixture.Gateway.RenamedGroups.Count);
        Assert.AreEqual(0, fixture.SessionGroups.Calls.Count);
    }

    [TestMethod]
    public async Task AliasUpdateSynchronizesBoundSessionGroupAfterAliasIsDurable()
    {
        var fixture = Fixture.Create(bound: true);
        fixture.Store.AllowUpdates = true;
        await BindSessionGroupAsync(
            fixture,
            "group-chat",
            "OpenCode｜alpha",
            ordinal: 2,
            preserveFuture: true);

        await fixture.Handler.HandleAsync(AliasIntent(
            "别名 #12345678 新名称",
            eventId: "event-group-rename"));

        CollectionAssert.AreEqual(
            new[] { ("group-chat", "OpenCode｜新名称") },
            fixture.Gateway.RenamedGroups.ToArray());
        Assert.AreEqual(1, fixture.SessionGroups.Calls.Count);
        Assert.AreEqual("group-chat", fixture.SessionGroups.Calls[0].ExpectedChatId);
        Assert.AreEqual("OpenCode｜新名称", fixture.SessionGroups.Calls[0].Name);
        var session = fixture.Store.Current.Sessions.Sessions["session-12345678"];
        Assert.AreEqual("新名称", session.ExtensionData!["alias"].GetString());
        Assert.AreEqual(
            "OpenCode｜新名称",
            session.ExtensionData["feishuChatName"].GetString());
        Assert.AreEqual("keep", session.ExtensionData["futureGroup"].GetString());
        StringAssert.Contains(fixture.Gateway.Replies.Single().Text, "@新名称");
    }

    [TestMethod]
    public async Task ClearingAliasRestoresProjectNameAndStoredOrdinal()
    {
        var fixture = Fixture.Create(bound: true);
        fixture.Store.AllowUpdates = true;
        await BindSessionGroupAsync(
            fixture,
            "group-chat",
            "OpenCode｜alpha",
            ordinal: 2);

        await fixture.Handler.HandleAsync(AliasIntent(
            "别名 #12345678 清除",
            eventId: "event-group-clear"));

        Assert.AreEqual(
            ("group-chat", "OpenCode｜project-one（2）"),
            fixture.Gateway.RenamedGroups.Single());
        Assert.AreEqual(
            "OpenCode｜project-one（2）",
            fixture.Store.Current.Sessions.Sessions["session-12345678"]
                .ExtensionData!["feishuChatName"].GetString());
        StringAssert.Contains(fixture.Gateway.Replies.Single().Text, "已清除");
    }

    [TestMethod]
    public async Task AliasUpdateSkipsFeishuWhenGroupAlreadyHasTargetName()
    {
        var fixture = Fixture.Create(bound: true);
        fixture.Store.AllowUpdates = true;
        await BindSessionGroupAsync(
            fixture,
            "group-chat",
            "OpenCode｜新名称",
            ordinal: 2);

        await fixture.Handler.HandleAsync(AliasIntent(
            "别名 #12345678 新名称",
            eventId: "event-group-current"));

        Assert.AreEqual(0, fixture.Gateway.RenamedGroups.Count);
        Assert.AreEqual(0, fixture.SessionGroups.Calls.Count);
        Assert.AreEqual(
            "新名称",
            fixture.Store.Current.Sessions.Sessions["session-12345678"]
                .ExtensionData!["alias"].GetString());
    }

    [TestMethod]
    public async Task FeishuRenameFailureKeepsDurableAliasAndRetryableStoredName()
    {
        var fixture = Fixture.Create(bound: true);
        fixture.Store.AllowUpdates = true;
        await BindSessionGroupAsync(
            fixture,
            "group-chat",
            "OpenCode｜alpha",
            ordinal: 1);
        fixture.Gateway.GroupRenameError =
            new HttpRequestException("synthetic rename failure");

        await fixture.Handler.HandleAsync(AliasIntent(
            "别名 #12345678 新名称",
            eventId: "event-group-api-failure"));

        var session = fixture.Store.Current.Sessions.Sessions["session-12345678"];
        Assert.AreEqual("新名称", session.ExtensionData!["alias"].GetString());
        Assert.AreEqual(
            "OpenCode｜alpha",
            session.ExtensionData["feishuChatName"].GetString());
        Assert.AreEqual(1, fixture.Gateway.RenamedGroups.Count);
        Assert.AreEqual(0, fixture.SessionGroups.Calls.Count);
        StringAssert.Contains(
            fixture.Gateway.Replies.Single().Text,
            "别名已保存，但飞书群名同步失败");
    }

    [TestMethod]
    public async Task GroupNameStateFailureRemainsRetryableAfterFeishuSucceeded()
    {
        var fixture = Fixture.Create(bound: true);
        fixture.Store.AllowUpdates = true;
        await BindSessionGroupAsync(
            fixture,
            "group-chat",
            "OpenCode｜alpha",
            ordinal: 1);
        fixture.SessionGroups.UpdateError = new IOException("synthetic store failure");

        await fixture.Handler.HandleAsync(AliasIntent(
            "别名 #12345678 新名称",
            eventId: "event-group-store-failure"));

        Assert.AreEqual(1, fixture.Gateway.RenamedGroups.Count);
        Assert.AreEqual(
            "OpenCode｜alpha",
            fixture.Store.Current.Sessions.Sessions["session-12345678"]
                .ExtensionData!["feishuChatName"].GetString());
        StringAssert.Contains(
            fixture.Gateway.Replies.Single().Text,
            "别名已保存，但飞书群名同步失败");

        fixture.SessionGroups.UpdateError = null;
        await fixture.Handler.HandleAsync(AliasIntent(
            "别名 #12345678 新名称",
            eventId: "event-group-store-retry"));

        Assert.AreEqual(2, fixture.Gateway.RenamedGroups.Count);
        Assert.AreEqual(
            "OpenCode｜新名称",
            fixture.Store.Current.Sessions.Sessions["session-12345678"]
                .ExtensionData!["feishuChatName"].GetString());
    }

    [TestMethod]
    public async Task GroupNameSynchronizationDoesNotSwallowCallerCancellation()
    {
        var fixture = Fixture.Create(bound: true);
        fixture.Store.AllowUpdates = true;
        await BindSessionGroupAsync(
            fixture,
            "group-chat",
            "OpenCode｜alpha",
            ordinal: 1);
        using var cancellation = new CancellationTokenSource();
        fixture.Gateway.AfterGroupRename = (_, _, _) =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            fixture.Handler.HandleAsync(
                AliasIntent(
                    "别名 #12345678 新名称",
                    eventId: "event-group-cancelled"),
                cancellation.Token));

        var session = fixture.Store.Current.Sessions.Sessions["session-12345678"];
        Assert.AreEqual("新名称", session.ExtensionData!["alias"].GetString());
        Assert.AreEqual(
            "OpenCode｜alpha",
            session.ExtensionData["feishuChatName"].GetString());
        Assert.AreEqual(1, fixture.Gateway.RenamedGroups.Count);
    }

    [TestMethod]
    public async Task ReplacedGroupBindingRejectsTheOldNameWrite()
    {
        var fixture = Fixture.Create(bound: true);
        fixture.Store.AllowUpdates = true;
        await BindSessionGroupAsync(
            fixture,
            "group-old",
            "OpenCode｜alpha",
            ordinal: 1);
        fixture.Gateway.AfterGroupRename = (_, _, cancellationToken) =>
            fixture.Store.UpdateAsync(
                current => NodeStoreBusinessStateMerger.PatchSessionExtensions(
                    current,
                    "session-12345678",
                    new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                    {
                        ["feishuChatId"] =
                            JsonSerializer.SerializeToElement("group-new"),
                    }),
                cancellationToken).AsTask();

        await fixture.Handler.HandleAsync(AliasIntent(
            "别名 #12345678 新名称",
            eventId: "event-group-rebound"));

        var session = fixture.Store.Current.Sessions.Sessions["session-12345678"];
        Assert.AreEqual("group-new", session.ExtensionData!["feishuChatId"].GetString());
        Assert.AreEqual(
            "OpenCode｜alpha",
            session.ExtensionData["feishuChatName"].GetString());
        StringAssert.Contains(
            fixture.Gateway.Replies.Single().Text,
            "会话群绑定已变化");
    }

    [TestMethod]
    public async Task AliasCommandReturnsValidationConflictAndPrivateChatErrors()
    {
        var fixture = Fixture.Create(bound: true);
        fixture.Store.AllowUpdates = true;

        await fixture.Handler.HandleAsync(AliasIntent(
            "别名 #12345678 two words",
            eventId: "event-invalid-alias"));
        fixture.SessionAliases.NextResult = new(
            null,
            new SessionStoreRecord
            {
                SessionId = "history-87654321",
                ShortId = "87654321",
                ProjectName = "history-project",
                Cwd = "K:\\workspace\\history-project",
                Status = SessionStatuses.Ended,
                LastSeenAt = "2026-08-07T00:00:00.000Z",
            },
            null);
        await fixture.Handler.HandleAsync(AliasIntent(
            "别名 #12345678 保留名",
            eventId: "event-conflict-alias"));
        await fixture.Handler.HandleAsync(AliasIntent(
            "别名 #12345678 群内名",
            chatType: "group",
            eventId: "event-group-alias"));
        await fixture.Handler.HandleAsync(AliasIntent(
            "别名 #bad 设置",
            eventId: "event-malformed-alias"));

        StringAssert.Contains(fixture.Gateway.Replies[0].Text, "不能包含空格");
        StringAssert.Contains(fixture.Gateway.Replies[1].Text, "已被会话");
        StringAssert.Contains(fixture.Gateway.Replies[1].Text, "#87654321");
        StringAssert.Contains(fixture.Gateway.Replies[2].Text, "只能在机器人私聊");
        StringAssert.Contains(fixture.Gateway.Replies[3].Text, "别名 #短ID 名称");
        Assert.AreEqual(2, fixture.SessionAliases.Calls.Count);
    }

    [TestMethod]
    public async Task UnboundOperatorCannotUseGlobalControls()
    {
        var fixture = Fixture.Create(bound: false);

        var messageResult = await fixture.Handler.HandleAsync(
            Intent(FeishuIntentTypes.CommandMenu));
        var cardResult = await fixture.Handler.HandleAsync(
            Intent(FeishuIntentTypes.CommandNew, chatType: "card"));

        Assert.IsNull(messageResult);
        Assert.AreEqual(1, fixture.Gateway.Replies.Count);
        StringAssert.Contains(fixture.Gateway.Replies[0].Text, "管理员账号");
        Assert.AreEqual("warning", cardResult?.ToastType);
        Assert.AreEqual(0, fixture.Gateway.Cards.Count);
    }

    [TestMethod]
    public async Task BoundOwnerPromptUsesMigratedCoordinator()
    {
        var fixture = Fixture.Create(bound: true);

        var result = await fixture.Handler.HandleAsync(
            Intent(FeishuIntentTypes.MessagePrompt));

        Assert.IsNull(result);
        Assert.AreEqual(1, fixture.Gateway.Replies.Count);
        StringAssert.Contains(fixture.Gateway.Replies[0].Text, "请先处理待审批操作");
        Assert.AreEqual(0, fixture.RuntimeCommands.Commands.Count);
    }

    [TestMethod]
    public async Task QuotedApprovalTextIsHandledBeforePromptRouting()
    {
        var fixture = Fixture.Create(bound: true);
        var result = await fixture.Handler.HandleAsync(new(
            "event-quoted",
            FeishuIntentTypes.MessagePrompt,
            "owner-1",
            "chat-1",
            "reply-message",
            "p2p",
            "trace-quoted",
            Text: "继续",
            Parameters: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["parentMessageId"] = "approval-card",
            }));

        Assert.IsNull(result);
        Assert.AreEqual(1, fixture.Gateway.Replies.Count);
        StringAssert.Contains(fixture.Gateway.Replies[0].Text, "等待审批");
        Assert.IsFalse(fixture.Gateway.Replies[0].Text.Contains(
            "尚未迁移",
            StringComparison.Ordinal));
        Assert.AreEqual(0, fixture.RuntimeCommands.Commands.Count);
    }

    [TestMethod]
    public async Task RetryStopValidatesCycleAndReturnsCoordinatorResult()
    {
        var fixture = Fixture.Create(bound: true);
        fixture.RuntimeRetries.StopResult = new(
            BridgeRetryStopKinds.Stopped,
            false,
            new FeishuCardRenderer().RuntimeLaunchCancelled(RuntimeNames.Codex));

        var stopped = await fixture.Handler.HandleAsync(RetryIntent());
        fixture.RuntimeRetries.StopResult = new(
            BridgeRetryStopKinds.Stopped,
            true,
            stopped!.Card);
        var running = await fixture.Handler.HandleAsync(RetryIntent());
        fixture.RuntimeRetries.StopResult = new(
            BridgeRetryStopKinds.AlreadyStopped,
            false,
            stopped.Card);
        var repeated = await fixture.Handler.HandleAsync(RetryIntent());
        fixture.RuntimeRetries.StopResult = new(BridgeRetryStopKinds.Stale, false);
        var stale = await fixture.Handler.HandleAsync(RetryIntent());
        var invalid = await fixture.Handler.HandleAsync(new(
            "event-invalid",
            FeishuIntentTypes.RetryStop,
            "owner-1",
            "chat-1",
            "card-message-1",
            "card",
            "trace-1"));

        Assert.AreEqual("success", stopped.ToastType);
        Assert.AreEqual("已停止自动重试。", stopped.ToastContent);
        Assert.IsNotNull(stopped.Card);
        StringAssert.Contains(running!.ToastContent, "已经发送");
        Assert.AreEqual("info", repeated!.ToastType);
        Assert.AreEqual("自动重试已经停止。", repeated.ToastContent);
        Assert.AreEqual("warning", stale!.ToastType);
        Assert.AreEqual("error", invalid!.ToastType);
        Assert.AreEqual(4, fixture.RuntimeRetries.StopCalls.Count);
        Assert.IsTrue(fixture.RuntimeRetries.StopCalls.All(call =>
            call == ("session-12345678", "cycle-1", "card-message-1")));
    }

    [TestMethod]
    public async Task PassiveModeFailsBeforeReadingProductionStore()
    {
        var fixture = Fixture.Create(bound: true, active: false);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            fixture.Handler.HandleAsync(Intent(FeishuIntentTypes.CommandMenu)));

        Assert.AreEqual(0, fixture.Store.Reads);
        Assert.AreEqual(0, fixture.Gateway.TotalOutbound);
    }

    private static FeishuIntent Intent(string intentType, string chatType = "p2p") => new(
        "event-1",
        intentType,
        "owner-1",
        "chat-1",
        "message-1",
        chatType,
        "trace-1",
        Text: "/");

    private static FeishuIntent RuntimeIntent(
        string intentType,
        string flowId,
        string runtime = RuntimeNames.Codex,
        string? projectName = null,
        string eventId = "event-1") => new(
            eventId,
            intentType,
            "owner-1",
            "chat-1",
            "card-message-1",
            "card",
            "trace-1",
        Parameters: RuntimeParameters(flowId, runtime, projectName));

    private static FeishuIntent AliasIntent(
        string text,
        string chatType = "p2p",
        string eventId = "event-alias") => new(
        eventId,
        FeishuIntentTypes.CommandAliases,
        "owner-1",
        "chat-1",
        $"message-{eventId}",
        chatType,
        $"trace-{eventId}",
        Text: text);

    private static FeishuIntent RetryIntent() => new(
        "event-retry",
        FeishuIntentTypes.RetryStop,
        "owner-1",
        "chat-1",
        "card-message-1",
        "card",
        "trace-1",
        Parameters: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sessionId"] = "session-12345678",
            ["retryCycleId"] = "cycle-1",
        });

    private static Task BindSessionGroupAsync(
        Fixture fixture,
        string chatId,
        string chatName,
        int ordinal,
        bool preserveFuture = false) =>
        fixture.Store.UpdateAsync(
            current => NodeStoreBusinessStateMerger.PatchSessionExtensions(
                current,
                "session-12345678",
                new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                {
                    ["feishuChatId"] = JsonSerializer.SerializeToElement(chatId),
                    ["feishuChatName"] = JsonSerializer.SerializeToElement(chatName),
                    ["feishuChatOrdinal"] = JsonSerializer.SerializeToElement(ordinal),
                    ["futureGroup"] = preserveFuture
                        ? JsonSerializer.SerializeToElement("keep")
                        : null,
                })).AsTask();

    private static IReadOnlyDictionary<string, string> RuntimeParameters(
        string flowId,
        string runtime,
        string? projectName)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["flowId"] = flowId,
            ["runtime"] = runtime,
            ["sourceMessageId"] = "source-message-1",
            ["chatId"] = "chat-1",
        };
        if (projectName is not null)
        {
            parameters["form.project_name"] = projectName;
        }
        return parameters;
    }

    private static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }

        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                "/d",
                "/c",
                "mklink",
                "/J",
                linkPath,
                targetPath,
            },
        }) ?? throw new AssertFailedException("无法启动目录联接测试进程。");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new AssertFailedException(
                $"无法创建目录联接：{process.StandardError.ReadToEnd()}");
        }
    }

    private static string CardJson(FeishuCardView card) =>
        card.Content.ToJsonString(new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    private sealed record Fixture(
        ActiveFeishuIntentHandler Handler,
        RecordingStoreOwner Store,
        RecordingFeishuGateway Gateway,
        RecordingRuntimeCommandGateway RuntimeCommands,
        RecordingRuntimeRetryCoordinator RuntimeRetries,
        RecordingSessionAliasStateOwner SessionAliases,
        RecordingSessionGroupStateOwner SessionGroups)
    {
        public static Fixture Create(
            bool bound,
            bool active = true,
            string? workspaceRoot = null)
        {
            var options = new BridgeHostOptions(
                Path.GetTempPath(),
                IPAddress.Loopback,
                0,
                active ? BridgeOwnershipMode.Active : BridgeOwnershipMode.Passive,
                "active-feishu-intent-test");
            var store = new RecordingStoreOwner(StoreSnapshot(bound, workspaceRoot));
            var gateway = new RecordingFeishuGateway();
            var runtimeCommands = new RecordingRuntimeCommandGateway();
            var runtimeRetries = new RecordingRuntimeRetryCoordinator();
            var business = new RecordingBusinessStateOwner(BusinessSnapshot());
            var sessionAliases = new RecordingSessionAliasStateOwner(store);
            var sessionGroups = new RecordingSessionGroupStateOwner(store);
            var launches = new RecordingLaunchCoordinator();
            var fileTransfers = new RecordingFileTransferCoordinator();
            var prompts = new ActiveFeishuPromptCoordinator(
                store,
                business,
                runtimeCommands,
                runtimeRetries,
                gateway,
                fileTransfers);
            var renderer = new FeishuCardRenderer();
            var interactions = new FeishuInteractionCoordinator(
                gateway,
                renderer,
                new InMemoryFeishuCardPatchLedger());
            var approvals = new ActiveFeishuApprovalCoordinator(
                new RejectingApprovalStateOwner(business.Snapshot),
                runtimeCommands,
                interactions);
            var inputs = new ActiveFeishuInputCoordinator(
                new RejectingInputStateOwner(business.Snapshot),
                runtimeCommands,
                interactions,
                renderer,
                gateway,
                new RejectingManagedHookResponseSink());
            return new(
                new(
                    options,
                    store,
                    business,
                    sessionAliases,
                    sessionGroups,
                    launches,
                    runtimeCommands,
                    runtimeRetries,
                    gateway,
                    renderer,
                    prompts,
                    approvals,
                    inputs),
                store,
                gateway,
                runtimeCommands,
                runtimeRetries,
                sessionAliases,
                sessionGroups);
        }
    }

    private static NodeStoreSnapshot StoreSnapshot(
        bool bound,
        string? workspaceRoot = null)
    {
        var binding = new BindingStoreDocument
        {
            OwnerOpenId = "owner-1",
            Users = bound
                ? new Dictionary<string, BindingStoreRecord>(StringComparer.Ordinal)
                {
                    ["owner-1"] = new()
                    {
                        OpenId = "owner-1",
                        ChatId = "chat-1",
                        ChatType = "p2p",
                        BoundAt = "2026-08-07T00:00:00.000Z",
                    },
                }
                : [],
        };
        var session = new SessionStoreRecord
        {
            SessionId = "session-12345678",
            ShortId = "12345678",
            ProjectName = "project-one",
            Cwd = "K:\\workspace\\project-one",
            Runtime = "opencode",
            Status = SessionStatuses.Waiting,
            OpenedAt = "2026-08-07T00:00:00.000Z",
            LastSeenAt = "2026-08-07T00:01:00.000Z",
            ExtensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["alias"] = JsonSerializer.SerializeToElement("alpha"),
            },
        };
        return new(
            binding,
            new SessionStoreDocument
            {
                Sessions = new Dictionary<string, SessionStoreRecord>(StringComparer.Ordinal)
                {
                    [session.SessionId] = session,
                },
            },
            new RouteStoreDocument
            {
                Messages = new Dictionary<string, MessageRouteStoreRecord>(
                    StringComparer.Ordinal)
                {
                    ["approval-card"] = new()
                    {
                        MessageId = "approval-card",
                        SessionId = "session-12345678",
                        RequestId = "approval-1",
                        ChatId = "chat-1",
                        Kind = "approval",
                        CreatedAt = "2026-08-07T00:00:00.000Z",
                    },
                },
            },
            new ApprovalStoreDocument
            {
                Requests = new Dictionary<string, ApprovalStoreRecord>(
                    StringComparer.Ordinal)
                {
                    ["approval-1"] = new()
                    {
                        RequestId = "approval-1",
                        SessionId = "session-12345678",
                        TurnId = "turn-1",
                        Cwd = "K:\\workspace\\project-one",
                        ToolName = "shell_command",
                        ToolPreview = "git status",
                        CreatedAt = "2026-08-07T00:00:00.000Z",
                        ExpiresAt = "2026-08-07T00:05:00.000Z",
                        Status = ApprovalStatuses.Pending,
                    },
                },
            },
            new SettingsStoreDocument { WorkspaceRoot = workspaceRoot ?? "K:\\workspace" },
            new ControlTokenStoreDocument());
    }

    private static BridgeBusinessStateSnapshot BusinessSnapshot()
    {
        var observedAt = DateTimeOffset.Parse("2026-08-07T00:00:00.000Z");
        return new(
            true,
            "production",
            3,
            0,
            new SessionDirectoryState(
                new Dictionary<string, SessionState>(StringComparer.Ordinal)
                {
                    ["session-12345678"] = new(
                        "session-12345678",
                        "opencode",
                        "K:\\workspace\\project-one",
                        SessionStatuses.Waiting,
                        observedAt,
                        observedAt),
                }),
            new ApprovalRegistryState(
                new Dictionary<string, ApprovalState>(StringComparer.Ordinal)
                {
                    ["approval-1"] = new(
                        "approval-1",
                        "session-12345678",
                        ApprovalStatuses.Pending,
                        observedAt,
                        observedAt.AddMinutes(5),
                        []),
                },
                new HashSet<string>(StringComparer.Ordinal)),
            new InputRegistryState(
                new Dictionary<string, InputRequestState>(StringComparer.Ordinal)
                {
                    ["input-1"] = new(
                        "input-1",
                        "session-12345678",
                        InputRequestStatuses.Pending,
                        observedAt,
                        observedAt.AddMinutes(5),
                        [new("q1", false, false, ["yes"])],
                        new Dictionary<string, IReadOnlyList<string>>(
                            StringComparer.Ordinal)),
                }));
    }

    private sealed class RecordingStoreOwner(NodeStoreSnapshot store) :
        IBridgeProductionStoreOwner
    {
        private NodeStoreSnapshot current = store;

        public int Reads { get; private set; }
        public int Updates { get; private set; }
        public bool AllowUpdates { get; set; }
        public NodeStoreSnapshot Current => current;

        public BridgeProductionStoreSnapshot Snapshot => new(
            BridgeProductionStoreState.Open,
            null,
            6);

        public ValueTask OpenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<NodeStoreSnapshot> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return ValueTask.FromResult(current);
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask UpdateAsync(
            Func<NodeStoreSnapshot, NodeStoreSnapshot> update,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!AllowUpdates)
            {
                throw new AssertFailedException("只读意图不应写入生产 Store。");
            }
            Updates++;
            current = update(current);
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

    private sealed class RecordingSessionAliasStateOwner(
        RecordingStoreOwner store) : IBridgeActiveSessionAliasStateOwner
    {
        public List<(string SessionId, string? Alias)> Calls { get; } = [];
        public BridgeSessionAliasUpdateResult? NextResult { get; set; }

        public async ValueTask<BridgeSessionAliasUpdateResult> UpdateSessionAliasAsync(
            string sessionId,
            string? alias,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((sessionId, alias));
            if (NextResult is { } next)
            {
                NextResult = null;
                return next;
            }
            if (alias is not null &&
                SessionAliasRules.ValidationError(alias) is { } validationError)
            {
                return new(null, null, validationError);
            }
            var normalizedAlias = alias is null
                ? null
                : SessionAliasRules.Normalize(alias);
            BridgeSessionAliasUpdateResult? result = null;
            await store.UpdateAsync(
                current =>
                {
                    if (!current.Sessions.Sessions.TryGetValue(sessionId, out var session))
                    {
                        result = new(null, null, "会话不存在或已经失效。");
                        return current;
                    }
                    var updated = NodeStoreBusinessStateMerger.PatchSessionExtensions(
                        current,
                        sessionId,
                        new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                        {
                            ["alias"] = normalizedAlias is null
                                ? null
                                : JsonSerializer.SerializeToElement(normalizedAlias),
                        });
                    result = new(updated.Sessions.Sessions[sessionId], null, null);
                    return updated;
                },
                cancellationToken);
            return result!;
        }
    }

    private sealed class RecordingSessionGroupStateOwner(
        RecordingStoreOwner store) : IBridgeActiveSessionGroupStateOwner
    {
        public List<(string SessionId, string ExpectedChatId, string Name)> Calls
        { get; } = [];
        public Exception? UpdateError { get; set; }

        public ValueTask<BridgeSessionGroupNameUpdateResult>
            EnsureSessionGroupOrdinalAsync(
                string sessionId,
                CancellationToken cancellationToken = default) =>
            throw new AssertFailedException(
                "别名群名同步测试不应分配会话群序号。");

        public ValueTask<BridgeSessionGroupNameUpdateResult>
            BindSessionGroupAsync(
                string sessionId,
                int expectedOrdinal,
                string expectedOwnerOpenId,
                string chatId,
                string name,
                DateTimeOffset createdAt,
                CancellationToken cancellationToken = default) =>
            throw new AssertFailedException(
                "别名群名同步测试不应创建会话群绑定。");

        public ValueTask<BridgeSessionGroupNameUpdateResult>
            RecordSessionGroupErrorAsync(
                string sessionId,
                int expectedOrdinal,
                string expectedOwnerOpenId,
                string error,
                DateTimeOffset observedAt,
                CancellationToken cancellationToken = default) =>
            throw new AssertFailedException(
                "别名群名同步测试不应记录会话群创建错误。");

        public ValueTask<BridgeSessionGroupNameUpdateResult>
            ClearSessionGroupErrorAsync(
                string sessionId,
                int expectedOrdinal,
                string expectedOwnerOpenId,
                CancellationToken cancellationToken = default) =>
            throw new AssertFailedException(
                "别名群名同步测试不应清除会话群创建错误。");

        public ValueTask<BridgeSessionGroupNameUpdateResult>
            ClearSessionGroupAsync(
                string sessionId,
                string expectedChatId,
                CancellationToken cancellationToken = default) =>
            throw new AssertFailedException(
                "别名群名同步测试不应解绑会话群。");

        public async ValueTask<BridgeSessionGroupNameUpdateResult>
            UpdateSessionGroupNameAsync(
                string sessionId,
                string expectedChatId,
                string name,
                CancellationToken cancellationToken = default)
        {
            Calls.Add((sessionId, expectedChatId, name));
            if (UpdateError is not null)
            {
                throw UpdateError;
            }

            BridgeSessionGroupNameUpdateResult? result = null;
            await store.UpdateAsync(
                current =>
                {
                    if (!current.Sessions.Sessions.TryGetValue(sessionId, out var session))
                    {
                        result = new(null, "会话不存在或已经失效。");
                        return current;
                    }
                    var chatId = session.ExtensionData is not null &&
                        session.ExtensionData.TryGetValue("feishuChatId", out var value) &&
                        value.ValueKind == JsonValueKind.String
                            ? value.GetString()
                            : null;
                    if (!string.Equals(
                            chatId,
                            expectedChatId,
                            StringComparison.Ordinal))
                    {
                        result = new(null, "会话群绑定已变化，请重试。");
                        return current;
                    }

                    var updated = NodeStoreBusinessStateMerger.PatchSessionExtensions(
                        current,
                        sessionId,
                        new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                        {
                            ["feishuChatName"] =
                                JsonSerializer.SerializeToElement(name),
                        });
                    result = new(updated.Sessions.Sessions[sessionId], null);
                    return updated;
                },
                cancellationToken);
            return result!;
        }
    }

    private sealed class RejectingApprovalStateOwner(
        BridgeBusinessStateSnapshot snapshot) : IBridgeActiveApprovalStateOwner
    {
        public BridgeBusinessStateSnapshot Snapshot { get; } = snapshot;

        public ValueTask<BridgeApprovalClaim?> TryClaimApprovalAsync(
            string requestId,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("意图处理器测试不应进入审批协调器。");

        public ValueTask ReleaseApprovalClaimAsync(
            string requestId,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("意图处理器测试不应进入审批协调器。");

        public ValueTask<BridgeApprovalDelivery?> RecordApprovalDeliveryAsync(
            string requestId,
            string sessionId,
            string messageId,
            string chatId,
            DateTimeOffset createdAt,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("意图处理器测试不应进入审批协调器。");

        public ValueTask<BridgeApprovalClaim?> ResolveClaimedApprovalAsync(
            string requestId,
            string sessionId,
            string resolution,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("意图处理器测试不应进入审批协调器。");

        public ValueTask<BridgeApprovalClaim?> DeferClaimedApprovalAsync(
            string requestId,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("意图处理器测试不应进入审批协调器。");
    }

    private sealed class RejectingInputStateOwner(
        BridgeBusinessStateSnapshot snapshot) : IBridgeActiveInputStateOwner
    {
        public BridgeBusinessStateSnapshot Snapshot { get; } = snapshot;

        public ValueTask<BridgeInputAnswerProgress?> TryRecordInputAnswerAsync(
            string requestId,
            string sessionId,
            string questionId,
            IReadOnlyList<string> answers,
            CancellationToken cancellationToken = default) => Unexpected<BridgeInputAnswerProgress?>();

        public ValueTask<BridgeInputClaim?> TryClaimInputAsync(
            string requestId,
            string sessionId,
            CancellationToken cancellationToken = default) => Unexpected<BridgeInputClaim?>();

        public ValueTask<BridgeInputClaim?> ResolveClaimedInputAsync(
            string requestId,
            string sessionId,
            CancellationToken cancellationToken = default) => Unexpected<BridgeInputClaim?>();

        public ValueTask<BridgeInputClaim?> DeferClaimedInputAsync(
            string requestId,
            string sessionId,
            CancellationToken cancellationToken = default) => Unexpected<BridgeInputClaim?>();

        public ValueTask ReleaseInputClaimAsync(
            string requestId,
            CancellationToken cancellationToken = default) => Unexpected();

        public ValueTask<BridgeInputClaim?> ResetClaimedInputAsync(
            string requestId,
            string sessionId,
            CancellationToken cancellationToken = default) => Unexpected<BridgeInputClaim?>();

        private static ValueTask Unexpected() =>
            ValueTask.FromException(new AssertFailedException(
                "意图处理器测试不应进入问答协调器。"));

        private static ValueTask<T> Unexpected<T>() =>
            ValueTask.FromException<T>(new AssertFailedException(
                "意图处理器测试不应进入问答协调器。"));
    }

    private sealed class RecordingLaunchCoordinator : IBridgeManagedRuntimeLaunchCoordinator
    {
        public BridgeManagedRuntimeLifecycleSnapshot Snapshot { get; } =
            new(1, 0, 0, 2);

        public BridgeManagedRuntimeLaunchRequest? Claim() => null;

        public BridgeManagedRuntimeLaunchCompletionResult Complete(
            BridgeManagedRuntimeLaunchCompletion completion) =>
            new(true);

        public Task DrainAsync(
            string sessionExternalId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RejectingManagedHookResponseSink : IManagedHookResponseSink
    {
        public bool IsReady(string runtime, string sessionExternalId) => false;

        public Task ResolveApprovalAsync(
            RuntimeCommandContext context,
            string runtime,
            string sessionExternalId,
            string requestId,
            string decision,
            CancellationToken cancellationToken = default) => Unexpected();

        public Task ResolveInputAsync(
            RuntimeCommandContext context,
            string runtime,
            string sessionExternalId,
            string requestId,
            IReadOnlyDictionary<string, IReadOnlyList<string>> answers,
            CancellationToken cancellationToken = default) => Unexpected();

        public Task DeferInputToLocalAsync(
            string runtime,
            string sessionExternalId,
            string requestId,
            CancellationToken cancellationToken = default) => Unexpected();

        private static Task Unexpected() =>
            Task.FromException(new AssertFailedException(
                "意图处理器测试不应回写 Managed Hook。"));
    }

    private sealed class RecordingRuntimeCommandGateway : IBridgeRuntimeCommandGateway
    {
        public List<RuntimeCommandEnvelope> Commands { get; } = [];
        public Exception? Error { get; set; }
        public Func<RuntimeCommandEnvelope, CancellationToken, Task>? Handler { get; set; }

        public bool IsReady(string runtime, RuntimeSession session) => false;

        public async Task DispatchAsync(
            RuntimeCommandEnvelope command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private sealed class RecordingRuntimeRetryCoordinator :
        IBridgeActiveRuntimeRetryCoordinator
    {
        public BridgeRetryStopResult StopResult { get; set; } =
            new(BridgeRetryStopKinds.Stale, false);

        public List<(string SessionId, string CycleId, string MessageId)> StopCalls
            { get; } = [];

        public bool HasActiveRetry(string sessionId) => false;

        public ValueTask BeginManualTurnAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public Task<BridgeRetryStopResult> StopAsync(
            string sessionId,
            string cycleId,
            string messageId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCalls.Add((sessionId, cycleId, messageId));
            return Task.FromResult(StopResult);
        }
    }

    private sealed class RecordingFeishuGateway : IFeishuGateway
    {
        public List<(string ChatId, string Text)> SentTexts { get; } = [];
        public List<(string MessageId, string Text)> Replies { get; } = [];
        public List<(string ChatId, FeishuCardView Card, string? IdempotencyKey)> Cards
        { get; } = [];
        public List<(string ChatId, string Name)> RenamedGroups { get; } = [];
        public int ReplyFailuresRemaining { get; set; }
        public Exception? GroupRenameError { get; set; }
        public Func<string, string, CancellationToken, Task>? AfterGroupRename
        { get; set; }
        public int TotalOutbound =>
            SentTexts.Count + Replies.Count + Cards.Count + RenamedGroups.Count;

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
            if (ReplyFailuresRemaining > 0)
            {
                ReplyFailuresRemaining--;
                throw new HttpRequestException("synthetic reply failure");
            }
            Replies.Add((messageId, text));
            return Task.FromResult($"reply-{Replies.Count}");
        }

        public Task<string> SendCardAsync(
            string chatId,
            FeishuCardView card,
            string? idempotencyKey = null,
            CancellationToken cancellationToken = default)
        {
            Cards.Add((chatId, card, idempotencyKey));
            return Task.FromResult($"card-{Cards.Count}");
        }

        public Task PatchCardAsync(
            string messageId,
            FeishuCardView card,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("全局只读意图不应更新既有卡片。");

        public Task<FeishuSessionGroup> CreateSessionGroupAsync(
            string ownerOpenId,
            string name,
            string description,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("全局只读意图不应创建会话群。");

        public async Task UpdateSessionGroupNameAsync(
            string chatId,
            string name,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RenamedGroups.Add((chatId, name));
            if (GroupRenameError is not null)
            {
                throw GroupRenameError;
            }
            if (AfterGroupRename is not null)
            {
                await AfterGroupRename(chatId, name, cancellationToken);
            }
        }

        public Task DeleteSessionGroupAsync(
            string chatId,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("全局只读意图不应删除会话群。");

        public Task<long> DownloadMessageResourceAsync(
            string messageId,
            string fileKey,
            string resourceType,
            string destinationPath,
            long maxBytes,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("全局只读意图不应下载附件。");

        public Task<string> SendLocalFileAsync(
            string chatId,
            string filePath,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("全局只读意图不应发送文件。");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"active-feishu-intent-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
