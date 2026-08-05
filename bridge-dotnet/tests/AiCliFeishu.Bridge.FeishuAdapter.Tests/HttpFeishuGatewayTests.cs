using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiCliFeishu.Bridge.Adapters.Feishu;

namespace AiCliFeishu.Bridge.FeishuAdapter.Tests;

[TestClass]
public sealed class HttpFeishuGatewayTests
{
    private static readonly DateTimeOffset Origin =
        new(2026, 8, 6, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task SendTextGetsTenantTokenAndUsesFeishuWireContract()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, Token("token-1"));
        handler.Enqueue(HttpStatusCode.OK, Message("message-1"));
        var gateway = Gateway(handler);

        var messageId = await gateway.SendTextAsync("chat-1", "你好");

        Assert.AreEqual("message-1", messageId);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual(
            "/open-apis/auth/v3/tenant_access_token/internal",
            handler.Requests[0].Uri.AbsolutePath);
        using var tokenBody = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.AreEqual("app-id", tokenBody.RootElement.GetProperty("app_id").GetString());
        Assert.AreEqual(
            "/open-apis/im/v1/messages",
            handler.Requests[1].Uri.AbsolutePath);
        Assert.AreEqual("receive_id_type=chat_id", handler.Requests[1].Uri.Query.TrimStart('?'));
        Assert.AreEqual("Bearer token-1", handler.Requests[1].Authorization);
        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.AreEqual("chat-1", body.RootElement.GetProperty("receive_id").GetString());
        Assert.AreEqual("text", body.RootElement.GetProperty("msg_type").GetString());
        var content = body.RootElement.GetProperty("content").GetString();
        using var contentJson = JsonDocument.Parse(content!);
        Assert.AreEqual("你好", contentJson.RootElement.GetProperty("text").GetString());
    }

    [TestMethod]
    public async Task TokenIsCachedAcrossReplyAndPatch()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, Token("token-1"));
        handler.Enqueue(HttpStatusCode.OK, Message("reply-1"));
        handler.Enqueue(HttpStatusCode.OK, "{\"code\":0}");
        var gateway = Gateway(handler);

        await gateway.ReplyTextAsync("source-1", "收到");
        await gateway.PatchCardAsync(
            "card-1",
            new(new JsonObject { ["header"] = "updated" }));

        Assert.AreEqual(3, handler.Requests.Count);
        Assert.AreEqual(HttpMethod.Post, handler.Requests[1].Method);
        Assert.AreEqual("/open-apis/im/v1/messages/source-1/reply", handler.Requests[1].Uri.AbsolutePath);
        Assert.AreEqual(HttpMethod.Patch, handler.Requests[2].Method);
        Assert.AreEqual("/open-apis/im/v1/messages/card-1", handler.Requests[2].Uri.AbsolutePath);
        Assert.AreEqual("Bearer token-1", handler.Requests[2].Authorization);
    }

    [TestMethod]
    public async Task SendCardIncludesUuidAndStringEncodedCard()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, Token("token-1"));
        handler.Enqueue(HttpStatusCode.OK, Message("card-1"));
        var gateway = Gateway(handler);

        await gateway.SendCardAsync(
            "chat-1",
            new(new JsonObject { ["config"] = new JsonObject { ["update_multi"] = true } }),
            "idempotency-1");

        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.AreEqual("interactive", body.RootElement.GetProperty("msg_type").GetString());
        Assert.AreEqual("idempotency-1", body.RootElement.GetProperty("uuid").GetString());
        var encoded = body.RootElement.GetProperty("content").GetString();
        using var card = JsonDocument.Parse(encoded!);
        Assert.IsTrue(card.RootElement.GetProperty("config").GetProperty("update_multi").GetBoolean());
    }

    [TestMethod]
    public async Task UnauthorizedResponseRefreshesTokenAndRetriesOnce()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, Token("expired-token"));
        handler.Enqueue(HttpStatusCode.Unauthorized, "{\"code\":99991663}");
        handler.Enqueue(HttpStatusCode.OK, Token("fresh-token"));
        handler.Enqueue(HttpStatusCode.OK, Message("message-1"));
        var gateway = Gateway(handler);

        await gateway.SendTextAsync("chat-1", "retry");

        Assert.AreEqual(4, handler.Requests.Count);
        Assert.AreEqual("Bearer expired-token", handler.Requests[1].Authorization);
        Assert.AreEqual("Bearer fresh-token", handler.Requests[3].Authorization);
    }

    [TestMethod]
    public async Task FeishuBusinessErrorIncludesCodeAndMessage()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, Token("token-1"));
        handler.Enqueue(HttpStatusCode.OK, "{\"code\":230001,\"msg\":\"invalid receive id\"}");
        var gateway = Gateway(handler);

        var error = await Assert.ThrowsExceptionAsync<HttpRequestException>(() =>
            gateway.SendTextAsync("chat-1", "hello"));

        StringAssert.Contains(error.Message, "230001");
        StringAssert.Contains(error.Message, "invalid receive id");
    }

    [TestMethod]
    public async Task SessionGroupOperationsUseFeishuChatContract()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, Token("token-1"));
        handler.Enqueue(HttpStatusCode.OK,
            "{\"code\":0,\"data\":{\"chat_id\":\"chat-new\",\"name\":\"项目群\"}}");
        handler.Enqueue(HttpStatusCode.OK, "{\"code\":0}");
        handler.Enqueue(HttpStatusCode.OK, "{\"code\":0}");
        var gateway = Gateway(handler);

        var group = await gateway.CreateSessionGroupAsync(
            "owner-1",
            "项目群",
            "AI CLI 会话");
        await gateway.UpdateSessionGroupNameAsync("chat-new", "项目群 #2");
        await gateway.DeleteSessionGroupAsync("chat-new");

        Assert.AreEqual(new FeishuSessionGroup("chat-new", "项目群"), group);
        Assert.AreEqual(HttpMethod.Post, handler.Requests[1].Method);
        Assert.AreEqual("/open-apis/im/v1/chats", handler.Requests[1].Uri.AbsolutePath);
        Assert.AreEqual("user_id_type=open_id", handler.Requests[1].Uri.Query.TrimStart('?'));
        using var create = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.AreEqual("owner-1", create.RootElement.GetProperty("owner_id").GetString());
        Assert.AreEqual(
            "owner-1",
            create.RootElement.GetProperty("user_id_list")[0].GetString());
        Assert.AreEqual("private", create.RootElement.GetProperty("chat_type").GetString());
        Assert.AreEqual(HttpMethod.Put, handler.Requests[2].Method);
        Assert.AreEqual("/open-apis/im/v1/chats/chat-new", handler.Requests[2].Uri.AbsolutePath);
        Assert.AreEqual(HttpMethod.Delete, handler.Requests[3].Method);
        Assert.AreEqual("Bearer token-1", handler.Requests[3].Authorization);
    }

    [TestMethod]
    public async Task DownloadResourceStreamsToDestinationAndPreservesWireQuery()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, Token("token-1"));
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3, 4]),
        });
        var gateway = Gateway(handler);
        var destination = TempPath("download", ".bin");
        try
        {
            var size = await gateway.DownloadMessageResourceAsync(
                "message/1",
                "file key",
                "file",
                destination,
                10);

            Assert.AreEqual(4L, size);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(destination));
            Assert.AreEqual(
                "/open-apis/im/v1/messages/message%2F1/resources/file%20key",
                handler.Requests[1].Uri.AbsolutePath);
            Assert.AreEqual("type=file", handler.Requests[1].Uri.Query.TrimStart('?'));
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [TestMethod]
    public async Task DownloadResourceDeletesPartialFileWhenStreamExceedsLimit()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, Token("token-1"));
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthByteContent([1, 2, 3, 4, 5]),
        });
        var gateway = Gateway(handler);
        var destination = TempPath("oversized", ".bin");
        try
        {
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
                gateway.DownloadMessageResourceAsync(
                    "message-1",
                    "file-1",
                    "file",
                    destination,
                    3));

            Assert.IsFalse(File.Exists(destination));
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [TestMethod]
    public async Task SendImageUploadsMultipartThenSendsResourceMessage()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, Token("token-1"));
        handler.Enqueue(HttpStatusCode.OK,
            "{\"code\":0,\"data\":{\"image_key\":\"image-1\"}}");
        handler.Enqueue(HttpStatusCode.OK, Message("message-image"));
        var gateway = Gateway(handler);
        var image = TempPath("image", ".png");
        await File.WriteAllBytesAsync(image, [1, 2, 3]);
        try
        {
            var messageId = await gateway.SendLocalFileAsync("chat-1", image);

            Assert.AreEqual("message-image", messageId);
            Assert.AreEqual("/open-apis/im/v1/images", handler.Requests[1].Uri.AbsolutePath);
            StringAssert.Contains(handler.Requests[1].ContentType!, "multipart/form-data");
            StringAssert.Contains(handler.Requests[1].Body!, "name=image_type");
            StringAssert.Contains(handler.Requests[1].Body!, "message");
            using var message = JsonDocument.Parse(handler.Requests[2].Body!);
            Assert.AreEqual("image", message.RootElement.GetProperty("msg_type").GetString());
            using var content = JsonDocument.Parse(message.RootElement.GetProperty("content").GetString()!);
            Assert.AreEqual("image-1", content.RootElement.GetProperty("image_key").GetString());
        }
        finally
        {
            File.Delete(image);
        }
    }

    [TestMethod]
    public async Task FileUploadRecreatesMultipartBodyAfterUnauthorizedResponse()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, Token("expired-token"));
        handler.Enqueue(HttpStatusCode.Unauthorized, "{\"code\":99991663}");
        handler.Enqueue(HttpStatusCode.OK, Token("fresh-token"));
        handler.Enqueue(HttpStatusCode.OK, "{\"code\":0,\"file_key\":\"file-1\"}");
        handler.Enqueue(HttpStatusCode.OK, Message("message-file"));
        var gateway = Gateway(handler);
        var file = TempPath("file", ".txt");
        await File.WriteAllTextAsync(file, "upload-body");
        try
        {
            var messageId = await gateway.SendLocalFileAsync("chat-1", file);

            Assert.AreEqual("message-file", messageId);
            Assert.AreEqual("Bearer expired-token", handler.Requests[1].Authorization);
            Assert.AreEqual("Bearer fresh-token", handler.Requests[3].Authorization);
            StringAssert.Contains(handler.Requests[1].Body!, "upload-body");
            StringAssert.Contains(handler.Requests[3].Body!, "upload-body");
            StringAssert.Contains(handler.Requests[3].Body!, "name=file_type");
            StringAssert.Contains(handler.Requests[3].Body!, "stream");
            StringAssert.Contains(handler.Requests[3].Body!, "name=file_name");
            using var message = JsonDocument.Parse(handler.Requests[4].Body!);
            Assert.AreEqual("file", message.RootElement.GetProperty("msg_type").GetString());
            using var content = JsonDocument.Parse(message.RootElement.GetProperty("content").GetString()!);
            Assert.AreEqual("file-1", content.RootElement.GetProperty("file_key").GetString());
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static HttpFeishuGateway Gateway(QueueHttpMessageHandler handler) => new(
        new HttpClient(handler),
        new("app-id", "app-secret", new Uri("https://open.feishu.test/")),
        () => Origin);

    private static string Token(string value) =>
        JsonSerializer.Serialize(new
        {
            code = 0,
            tenant_access_token = value,
            expire = 7_200,
        });

    private static string Message(string messageId) =>
        JsonSerializer.Serialize(new { code = 0, data = new { message_id = messageId } });

    private static string TempPath(string name, string extension) => Path.Combine(
        Path.GetTempPath(),
        $"ai-cli-feishu-{name}-{Guid.NewGuid():N}{extension}");
}
