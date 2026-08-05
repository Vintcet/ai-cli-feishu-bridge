using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;

namespace AiCliFeishu.Bridge.FeishuAdapter.Tests;

[TestClass]
public sealed class FeishuEventNormalizerTests
{
    [TestMethod]
    public void SlashNormalizesToGlobalMenuWithoutSessionTarget()
    {
        var normalizer = NewNormalizer();

        var result = normalizer.NormalizeMessage(
            "event-1",
            "trace-1",
            Json("""
                {
                  "sender":{"sender_id":{"open_id":"owner"}},
                  "message":{
                    "message_id":"message-1",
                    "chat_id":"chat-1",
                    "chat_type":"group",
                    "message_type":"text",
                    "content":"{\"text\":\"/\"}"
                  }
                }
                """));

        Assert.IsTrue(result.IsAccepted);
        Assert.AreEqual(FeishuIntentTypes.CommandMenu, result.Intent!.IntentType);
        Assert.AreEqual("/", result.Intent.Text);
        Assert.IsFalse(result.Intent.Parameters!.ContainsKey("sessionId"));
    }

    [TestMethod]
    [DataRow("/new", FeishuIntentTypes.CommandNew)]
    [DataRow("/新建", FeishuIntentTypes.CommandNew)]
    [DataRow("新建", FeishuIntentTypes.CommandNew)]
    [DataRow("/工作区", FeishuIntentTypes.CommandWorkspace)]
    [DataRow("/状态", FeishuIntentTypes.CommandStatus)]
    [DataRow("/会话管理", FeishuIntentTypes.CommandSessions)]
    [DataRow("/会话别名", FeishuIntentTypes.CommandAliases)]
    [DataRow("/帮助", FeishuIntentTypes.CommandHelp)]
    [DataRow("请继续", FeishuIntentTypes.MessagePrompt)]
    public void TextCommandsNormalizeToStandardIntent(string text, string expected)
    {
        var normalizer = NewNormalizer();
        var content = JsonSerializer.Serialize(new { text });
        var payload = JsonSerializer.Serialize(new
        {
            sender = new { sender_id = new { open_id = "owner" } },
            message = new
            {
                message_id = Guid.NewGuid().ToString("N"),
                chat_id = "chat-1",
                chat_type = "p2p",
                message_type = "text",
                content,
            },
        });

        var result = normalizer.NormalizeMessage(
            Guid.NewGuid().ToString("N"),
            "trace-1",
            Json(payload));

        Assert.AreEqual(expected, result.Intent!.IntentType);
    }

    [TestMethod]
    public void MessagePreservesParentAndAttachmentMetadata()
    {
        var result = NewNormalizer().NormalizeMessage(
            "event-file",
            "trace-file",
            Json("""
                {
                  "sender":{"sender_id":{"open_id":"owner"}},
                  "message":{
                    "message_id":"message-file",
                    "parent_id":"approval-card",
                    "chat_id":"chat-1",
                    "chat_type":"group",
                    "message_type":"file",
                    "content":"{\"file_key\":\"file-1\",\"file_name\":\"report.txt\"}"
                  }
                }
                """));

        Assert.AreEqual("approval-card", result.Intent!.Parameters!["parentMessageId"]);
        var attachment = result.Intent.Attachments!.Single();
        Assert.AreEqual("file", attachment.Kind);
        Assert.AreEqual("file-1", attachment.Key);
        Assert.AreEqual("report.txt", attachment.Name);
    }

    [TestMethod]
    public void DuplicateEventIsRejectedBeforeItCanPublishAgain()
    {
        var normalizer = NewNormalizer();
        var payload = Json("""
            {
              "sender":{"sender_id":{"open_id":"owner"}},
              "message":{
                "message_id":"message-1","chat_id":"chat-1",
                "message_type":"text","content":"{\"text\":\"hello\"}"
              }
            }
            """);

        var first = normalizer.NormalizeMessage("same-event", "trace-1", payload);
        var duplicate = normalizer.NormalizeMessage("same-event", "trace-1", payload);

        Assert.IsTrue(first.IsAccepted);
        Assert.IsTrue(duplicate.Duplicate);
        Assert.IsNull(duplicate.Intent);
    }

    [TestMethod]
    public void ApprovalObjectActionNormalizesAndMapsResolution()
    {
        var result = NewNormalizer().NormalizeCardAction(
            "event-card",
            "trace-card",
            Json("""
                {
                  "operator":{"open_id":"owner"},
                  "context":{"open_message_id":"card-1","open_chat_id":"chat-1"},
                  "action":{"value":{
                    "action":"approval_allow",
                    "requestId":"approval-1",
                    "sessionId":"session-1"
                  }}
                }
                """));

        Assert.AreEqual(FeishuIntentTypes.ApprovalResolve, result.Intent!.IntentType);
        Assert.AreEqual("allow", result.Intent.Parameters!["resolution"]);
        Assert.AreEqual("approval-1", result.Intent.Parameters["requestId"]);
    }

    [TestMethod]
    public void StringActionAndLegacyContextAreAccepted()
    {
        var result = NewNormalizer().NormalizeCardAction(
            "event-card",
            "trace-card",
            Json("""
                {
                  "operator":{"open_id":"owner"},
                  "open_message_id":"card-1",
                  "open_chat_id":"chat-1",
                  "action":{"value":"{\"action\":\"input_answer\",\"requestId\":\"input-1\",\"sessionId\":\"session-1\",\"questionId\":\"q1\",\"answer\":\"Codex\"}"}
                }
                """));

        Assert.AreEqual(FeishuIntentTypes.InputAnswer, result.Intent!.IntentType);
        Assert.AreEqual("Codex", result.Intent.Parameters!["answer"]);
    }

    [TestMethod]
    public void RuntimeSubmitPreservesFormValues()
    {
        var result = NewNormalizer().NormalizeCardAction(
            "event-card",
            "trace-card",
            Json("""
                {
                  "operator":{"open_id":"owner"},
                  "context":{"open_message_id":"card-1","open_chat_id":"chat-1"},
                  "action":{
                    "value":{"action":"runtime_new_submit","flowId":"flow-1","runtime":"codex"},
                    "form_value":{"project_name":"新项目"}
                  }
                }
                """));

        Assert.AreEqual(FeishuIntentTypes.RuntimeNewSubmit, result.Intent!.IntentType);
        Assert.AreEqual("新项目", result.Intent.Parameters!["form.project_name"]);
    }

    [TestMethod]
    public void CommandMenuActionNormalizesWithoutRuntimeSessionContext()
    {
        var result = NewNormalizer().NormalizeCardAction(
            "event-menu",
            "trace-menu",
            CardPayload(FeishuCardActions.CommandNew, ""));

        Assert.AreEqual(FeishuIntentTypes.CommandNew, result.Intent!.IntentType);
        Assert.AreEqual("chat-1", result.Intent.ChatId);
        Assert.IsFalse(result.Intent.Parameters!.ContainsKey("sessionId"));
    }

    [TestMethod]
    public void UnknownOrIncompleteCardActionIsRejected()
    {
        var unknown = NewNormalizer().NormalizeCardAction(
            "event-unknown",
            "trace-card",
            CardPayload("future_action", "\"requestId\":\"approval-1\""));
        var incomplete = NewNormalizer().NormalizeCardAction(
            "event-incomplete",
            "trace-card",
            CardPayload("approval_deny", "\"requestId\":\"approval-1\""));

        StringAssert.Contains(unknown.Error!, "无法识别");
        StringAssert.Contains(incomplete.Error!, "sessionId");
    }

    [TestMethod]
    public void DeduplicatorEvictsOldestEntryAtCapacity()
    {
        var deduplicator = new InMemoryFeishuInboundDeduplicator(2);

        Assert.IsTrue(deduplicator.TryClaim("one"));
        Assert.IsTrue(deduplicator.TryClaim("two"));
        Assert.IsFalse(deduplicator.TryClaim("one"));
        Assert.IsTrue(deduplicator.TryClaim("three"));
        Assert.IsTrue(deduplicator.TryClaim("one"));
    }

    [TestMethod]
    public void ReleasedEventCanBeClaimedAgainAfterTransientFailure()
    {
        var deduplicator = new InMemoryFeishuInboundDeduplicator();

        Assert.IsTrue(deduplicator.TryClaim("event-1"));
        deduplicator.Release("event-1");

        Assert.IsTrue(deduplicator.TryClaim("event-1"));
    }

    [TestMethod]
    public void ReleasedClaimDoesNotLeaveStaleEvictionEntry()
    {
        var deduplicator = new InMemoryFeishuInboundDeduplicator(2);

        Assert.IsTrue(deduplicator.TryClaim("event-1"));
        deduplicator.Release("event-1");
        Assert.IsTrue(deduplicator.TryClaim("event-1"));
        Assert.IsTrue(deduplicator.TryClaim("event-2"));

        Assert.IsFalse(deduplicator.TryClaim("event-1"));
    }

    private static FeishuEventNormalizer NewNormalizer() =>
        new(new InMemoryFeishuInboundDeduplicator());

    private static JsonElement CardPayload(string action, string properties)
    {
        using var extra = JsonDocument.Parse($"{{{properties}}}");
        var value = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["action"] = action,
        };
        foreach (var property in extra.RootElement.EnumerateObject())
        {
            value[property.Name] = property.Value.GetString()!;
        }
        return Json(JsonSerializer.Serialize(new
        {
            @operator = new { open_id = "owner" },
            context = new { open_message_id = "card-1", open_chat_id = "chat-1" },
            action = new { value },
        }));
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
