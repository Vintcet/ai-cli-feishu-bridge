using System.Net;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.OpenCode;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.RuntimeAdapters.Tests;

[TestClass]
public sealed class OpenCodeHttpTransportTests
{
    private static readonly RuntimeCommandContext Context =
        new("command-http", "trace-http", "correlation-http");

    [TestMethod]
    public async Task PromptUsesExistingEndpointPathDirectoryAndPayload()
    {
        var setup = Setup();
        setup.Handler.Enqueue(HttpStatusCode.NoContent);

        await setup.Transport.SendPromptAsync(Context, "session/1", "继续");

        var request = setup.Handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("/session/session%2F1/prompt_async", request.Uri.AbsolutePath);
        StringAssert.Contains(request.Uri.Query, "directory=C%3A%2Frepo%20space");
        using var body = JsonDocument.Parse(request.Body!);
        Assert.AreEqual("text", body.RootElement.GetProperty("parts")[0]
            .GetProperty("type").GetString());
        Assert.AreEqual("继续", body.RootElement.GetProperty("parts")[0]
            .GetProperty("text").GetString());
    }

    [TestMethod]
    public async Task ApprovalFallsBackFromV2ToModernAndLegacyInOrder()
    {
        var setup = Setup();
        setup.Handler.Enqueue(HttpStatusCode.NotFound);
        setup.Handler.Enqueue(HttpStatusCode.MethodNotAllowed);
        setup.Handler.Enqueue(HttpStatusCode.OK);

        await setup.Transport.ResolveApprovalAsync(
            Context,
            "session-1",
            "permission/1",
            "allow_session");

        CollectionAssert.AreEqual(
            new[]
            {
                "/api/session/session-1/permission/permission%2F1/reply",
                "/permission/permission%2F1/reply",
                "/session/session-1/permissions/permission%2F1",
            },
            setup.Handler.Requests.Select(request => request.Uri.AbsolutePath).ToArray());
        Assert.AreEqual("", setup.Handler.Requests[0].Uri.Query);
        StringAssert.Contains(setup.Handler.Requests[1].Uri.Query, "directory=");
        using var v2 = JsonDocument.Parse(setup.Handler.Requests[0].Body!);
        using var modern = JsonDocument.Parse(setup.Handler.Requests[1].Body!);
        using var legacy = JsonDocument.Parse(setup.Handler.Requests[2].Body!);
        Assert.AreEqual("always", v2.RootElement.GetProperty("reply").GetString());
        Assert.AreEqual("always", modern.RootElement.GetProperty("reply").GetString());
        Assert.AreEqual("always", legacy.RootElement.GetProperty("response").GetString());
    }

    [TestMethod]
    public async Task NonCompatibilityHttpFailureStopsFallbackAndIncludesBody()
    {
        var setup = Setup();
        setup.Handler.Enqueue(HttpStatusCode.InternalServerError, "server exploded");

        var error = await Assert.ThrowsExceptionAsync<HttpRequestException>(() =>
            setup.Transport.ResolveApprovalAsync(
                Context,
                "session-1",
                "permission-1",
                "deny"));

        StringAssert.Contains(error.Message, "server exploded");
        Assert.AreEqual(1, setup.Handler.Requests.Count);
    }

    [TestMethod]
    public async Task InputReplyPreservesExplicitQuestionOrder()
    {
        var setup = Setup();
        setup.Handler.Enqueue(HttpStatusCode.OK);

        await setup.Transport.ResolveInputAsync(
            Context,
            "session-1",
            "question-1",
            new IReadOnlyList<string>[] { ["第一题"], ["第二题-A", "第二题-B"] });

        var request = setup.Handler.Requests.Single();
        Assert.AreEqual("/question/question-1/reply", request.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(request.Body!);
        var answers = body.RootElement.GetProperty("answers");
        Assert.AreEqual("第一题", answers[0][0].GetString());
        Assert.AreEqual("第二题-A", answers[1][0].GetString());
        Assert.AreEqual("第二题-B", answers[1][1].GetString());
    }

    [TestMethod]
    public async Task LaunchAndResumeWaitForReadyBeforeSendingPrompt()
    {
        var launch = Setup(initiallyReady: false);
        launch.Handler.Enqueue(HttpStatusCode.NoContent);
        await launch.Transport.LaunchAsync(
            Context,
            "new-session",
            "C:/repo space",
            "开始",
            elevated: true);
        CollectionAssert.AreEqual(
            new[]
            {
                "launch:command-http:new-session:C:/repo space:True",
                "wait:command-http:new-session",
            },
            launch.Lifecycle.Calls.ToArray());
        Assert.AreEqual(1, launch.Handler.Requests.Count);

        var resume = Setup(initiallyReady: false);
        resume.Handler.Enqueue(HttpStatusCode.NoContent);
        await resume.Transport.ResumeAsync(Context, "session-1", "继续");
        CollectionAssert.AreEqual(
            new[]
            {
                "resume:command-http:session-1:",
                "wait:command-http:session-1",
            },
            resume.Lifecycle.Calls.ToArray());
        Assert.AreEqual(1, resume.Handler.Requests.Count);
    }

    private static TransportSetup Setup(bool initiallyReady = true)
    {
        var handler = new QueueHttpMessageHandler();
        var endpoint = new OpenCodeEndpoint(
            new Uri("http://127.0.0.1:43210/"),
            "C:/repo space",
            Ready: true);
        var endpoints = new FakeOpenCodeEndpointDirectory();
        if (initiallyReady)
        {
            endpoints.Sessions["session-1"] = endpoint;
            endpoints.Sessions["session/1"] = endpoint;
        }
        var lifecycle = new FakeOpenCodeLifecycle(endpoints, endpoint);
        var transport = new HttpOpenCodeTransport(
            new HttpClient(handler),
            endpoints,
            lifecycle);
        return new(handler, lifecycle, transport);
    }

    private sealed record TransportSetup(
        QueueHttpMessageHandler Handler,
        FakeOpenCodeLifecycle Lifecycle,
        HttpOpenCodeTransport Transport);
}
