using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.RuntimeAdapters.Tests;

[TestClass]
public sealed class ManagedTerminalTransportTests
{
    private static readonly RuntimeCommandContext Context =
        new("command-pipe", "trace-pipe", "correlation-pipe");

    [TestMethod]
    public async Task SendsExistingNamedPipeJsonLineAndAcceptsSuccess()
    {
        var pipeName = $"AiCliFeishu.test_{Guid.NewGuid():N}";
        await using var server = Server(pipeName);
        var exchange = ExchangeAsync(server, "{\"ok\":true,\"error\":null}");
        var terminalId = pipeName["AiCliFeishu.".Length..];

        await new NamedPipeManagedTerminalTransport(TimeSpan.FromSeconds(2)).SendAsync(
            Context,
            new(terminalId, "session-1", Ready: true),
            "  第一行\r\n第二行  ",
            ManagedTerminalSubmitMode.Queue);
        var requestLine = await exchange;

        using var request = JsonDocument.Parse(requestLine!);
        Assert.AreEqual("prompt", request.RootElement.GetProperty("type").GetString());
        Assert.AreEqual("第一行  第二行", request.RootElement.GetProperty("prompt").GetString());
        Assert.AreEqual("queue", request.RootElement.GetProperty("submitMode").GetString());
    }

    [TestMethod]
    public async Task TerminalErrorAndMalformedResponseAreNotReportedAsSuccess()
    {
        var error = await SendWithResponseAsync("{\"ok\":false,\"error\":\"终端忙碌\"}");
        StringAssert.Contains(error.Message, "终端忙碌");

        var malformed = await SendWithResponseAsync("not-json");
        Assert.IsInstanceOfType<JsonException>(malformed);
    }

    [TestMethod]
    public async Task CancellationStopsWaitingForTerminalResponse()
    {
        var pipeName = $"AiCliFeishu.test_{Guid.NewGuid():N}";
        await using var server = Server(pipeName);
        var exchange = ExchangeAsync(server, response: null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var terminalId = pipeName["AiCliFeishu.".Length..];

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            new NamedPipeManagedTerminalTransport(TimeSpan.FromSeconds(5)).SendAsync(
                Context,
                new(terminalId, "session-1", Ready: true),
                "继续",
                ManagedTerminalSubmitMode.Steer,
                cancellation.Token));
        await exchange;
    }

    [TestMethod]
    public async Task NullPromptIsRejectedBeforeConnectingToThePipe()
    {
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(() =>
            new NamedPipeManagedTerminalTransport(TimeSpan.FromSeconds(2)).SendAsync(
                Context,
                new("terminal_12345678", "session-1", Ready: true),
                null!,
                ManagedTerminalSubmitMode.Steer));
    }

    private static async Task<Exception> SendWithResponseAsync(string response)
    {
        var pipeName = $"AiCliFeishu.test_{Guid.NewGuid():N}";
        await using var server = Server(pipeName);
        var exchange = ExchangeAsync(server, response);
        var terminalId = pipeName["AiCliFeishu.".Length..];
        Exception error;
        try
        {
            await new NamedPipeManagedTerminalTransport(TimeSpan.FromSeconds(2)).SendAsync(
                Context,
                new(terminalId, "session-1", Ready: true),
                "继续",
                ManagedTerminalSubmitMode.Steer);
            throw new AssertFailedException("预期托管终端响应会导致异常。");
        }
        catch (Exception exception) when (exception is not AssertFailedException)
        {
            error = exception;
        }
        await exchange;
        return error;
    }

    private static NamedPipeServerStream Server(string pipeName) => new(
        pipeName,
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);

    private static async Task<string?> ExchangeAsync(
        NamedPipeServerStream server,
        string? response)
    {
        await server.WaitForConnectionAsync();
        using var reader = new StreamReader(
            server,
            new UTF8Encoding(false),
            false,
            1_024,
            leaveOpen: true);
        var line = await reader.ReadLineAsync();
        if (response is not null)
        {
            await using var writer = new StreamWriter(
                server,
                new UTF8Encoding(false),
                1_024,
                leaveOpen: true)
            {
                AutoFlush = true,
            };
            await writer.WriteLineAsync(response);
        }
        else
        {
            await Task.Delay(500);
        }
        return line;
    }
}
