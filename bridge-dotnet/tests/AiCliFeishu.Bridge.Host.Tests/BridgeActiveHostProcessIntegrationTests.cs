using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class BridgeActiveHostProcessIntegrationTests
{
    [TestMethod]
    public async Task PublishedActiveHostStartsAndStopsOnlyWithIsolatedDurableEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("真实单文件 Active Host 进程演练只在 Windows 运行。");
        }

        var executable = Path.Combine(
            AppContext.BaseDirectory,
            "active-host",
            "AiCliFeishuBridgeHost.exe");
        Assert.IsTrue(File.Exists(executable), "测试输出缺少已发布的真实 C# Host。");

        var root = Path.Combine(
            Path.GetTempPath(),
            $"bridge-active-host-process-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(root, "data");
        Directory.CreateDirectory(dataDirectory);
        var controlToken = Convert.ToHexString(Guid.NewGuid().ToByteArray()) +
            Convert.ToHexString(Guid.NewGuid().ToByteArray());
        await File.WriteAllTextAsync(
            Path.Combine(dataDirectory, "control-token.json"),
            JsonSerializer.Serialize(new { token = controlToken }));

        using var proxyCancellation = new CancellationTokenSource();
        var blockedProxyListener = new TcpListener(IPAddress.Loopback, 0);
        blockedProxyListener.Start();
        var blockedProxyPort =
            ((IPEndPoint)blockedProxyListener.LocalEndpoint).Port;
        var blockedProxyTask = RejectProxyConnectionsAsync(
            blockedProxyListener,
            proxyCancellation.Token);
        var port = ReservePort(excluding: blockedProxyPort);
        const string operationId = "isolated-active-startup";
        const string instanceName = "isolated-active";
        Process? process = null;
        Task<string>? outputTask = null;
        Task<string>? errorTask = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            AddArgument(startInfo, "--data-directory", dataDirectory);
            AddArgument(startInfo, "--listen", "127.0.0.1");
            AddArgument(startInfo, "--port", port.ToString(CultureInfo.InvariantCulture));
            AddArgument(startInfo, "--ownership", "active");
            AddArgument(startInfo, "--instance", instanceName);
            AddArgument(startInfo, "--cutover-operation", operationId);
            startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
            startInfo.Environment["FEISHU_APP_ID"] = "isolated-app-id";
            startInfo.Environment["FEISHU_APP_SECRET"] = "isolated-app-secret";
            var blockedProxy = $"http://127.0.0.1:{blockedProxyPort}";
            foreach (var variable in new[]
                     {
                         "HTTP_PROXY",
                         "HTTPS_PROXY",
                         "ALL_PROXY",
                         "http_proxy",
                         "https_proxy",
                         "all_proxy",
                     })
            {
                startInfo.Environment[variable] = blockedProxy;
            }
            startInfo.Environment["NO_PROXY"] = "127.0.0.1,localhost";
            startInfo.Environment["no_proxy"] = "127.0.0.1,localhost";

            process = Process.Start(startInfo) ??
                throw new InvalidOperationException("真实 C# Active Host 未能启动。");
            outputTask = process.StandardOutput.ReadToEndAsync();
            errorTask = process.StandardError.ReadToEndAsync();
            await WriteBoundCheckpointAsync(
                dataDirectory,
                operationId,
                instanceName,
                process.Id);

            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
            };
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
                Timeout = TimeSpan.FromSeconds(2),
            };
            using var health = await WaitForReadyAsync(
                client,
                controlToken,
                process,
                outputTask,
                errorTask);
            var rootHealth = health.RootElement;
            Assert.IsTrue(rootHealth.GetProperty("ok").GetBoolean());
            Assert.AreEqual("dotnet", rootHealth.GetProperty("hostKind").GetString());
            Assert.AreEqual("active", rootHealth.GetProperty("ownershipMode").GetString());
            Assert.IsTrue(rootHealth.GetProperty("activeOwner").GetBoolean());
            Assert.AreEqual(instanceName, rootHealth.GetProperty("instanceName").GetString());
            Assert.AreEqual(process.Id, rootHealth.GetProperty("processId").GetInt32());
            var components = rootHealth.GetProperty("components")
                .EnumerateArray()
                .ToDictionary(
                    item => item.GetProperty("name").GetString()!,
                    item => item.GetProperty("status").GetString()!,
                    StringComparer.Ordinal);
            Assert.AreEqual("ready", components["production-owner"]);
            Assert.AreEqual("ready", components["production-store"]);
            Assert.AreEqual("ready", components["persistent-business-state-owner"]);
            Assert.AreEqual("ready", components["feishu-credentials"]);
            Assert.AreEqual("ready", components["feishu-event-pump"]);

            using var statusRequest = new HttpRequestMessage(
                HttpMethod.Get,
                "control/status");
            statusRequest.Headers.Add(
                BridgeControlApi.ControlTokenHeader,
                controlToken);
            using var statusResponse = await client.SendAsync(statusRequest);
            Assert.AreEqual(HttpStatusCode.OK, statusResponse.StatusCode);
            using var status = JsonDocument.Parse(
                await statusResponse.Content.ReadAsStringAsync());
            Assert.AreEqual(
                "loaded",
                status.RootElement.GetProperty("store").GetProperty("status").GetString());
            Assert.IsTrue(
                status.RootElement.GetProperty("businessState")
                    .GetProperty("initialized").GetBoolean());
            Assert.IsTrue(
                status.RootElement.GetProperty("boundaries")
                    .GetProperty("runtimeCommandsEnabled").GetBoolean());

            var leasePath = Path.Combine(dataDirectory, "bridge-active-owner.lock");
            var ownerPath = Path.Combine(leasePath, "owner.json");
            Assert.IsTrue(File.Exists(ownerPath));
            using (var owner = JsonDocument.Parse(await File.ReadAllTextAsync(ownerPath)))
            {
                Assert.AreEqual("dotnet", owner.RootElement.GetProperty("hostKind").GetString());
                Assert.AreEqual(process.Id, owner.RootElement.GetProperty("processId").GetInt32());
                Assert.AreEqual(
                    instanceName,
                    owner.RootElement.GetProperty("instanceName").GetString());
            }

            using var shutdown = new HttpRequestMessage(
                HttpMethod.Post,
                "control/shutdown")
            {
                Content = JsonContent.Create(new { }),
            };
            shutdown.Headers.Add(BridgeControlApi.ControlTokenHeader, controlToken);
            shutdown.Headers.Add(BridgeControlApi.ExpectedHostKindHeader, "dotnet");
            shutdown.Headers.Add(BridgeControlApi.ManagementApiVersionHeader, "1");
            shutdown.Headers.Add(
                BridgeControlApi.ExpectedProcessIdHeader,
                process.Id.ToString(CultureInfo.InvariantCulture));
            using var shutdownResponse = await client.SendAsync(shutdown);
            Assert.AreEqual(HttpStatusCode.Accepted, shutdownResponse.StatusCode);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(0, process.ExitCode, await FailureOutputAsync(outputTask, errorTask));
            Assert.IsFalse(Directory.Exists(leasePath));
            Assert.IsTrue(File.Exists(Path.Combine(dataDirectory, "settings.json")));
            Assert.IsTrue(File.Exists(Path.Combine(dataDirectory, "sessions.json")));
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                process.Dispose();
            }
            proxyCancellation.Cancel();
            blockedProxyListener.Stop();
            try
            {
                await blockedProxyTask;
            }
            catch (OperationCanceledException)
            {
            }
            TryDeleteDirectory(root);
        }
    }

    private static async Task<JsonDocument> WaitForReadyAsync(
        HttpClient client,
        string controlToken,
        Process process,
        Task<string> outputTask,
        Task<string> errorTask)
    {
        for (var attempt = 1; attempt <= 120; attempt++)
        {
            if (process.HasExited)
            {
                Assert.Fail(
                    $"真实 C# Active Host 提前退出（code={process.ExitCode}）。\n" +
                    await FailureOutputAsync(outputTask, errorTask));
            }
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "health");
                request.Headers.Add(BridgeControlApi.ControlTokenHeader, controlToken);
                using var response = await client.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var document = JsonDocument.Parse(
                        await response.Content.ReadAsStringAsync());
                    if (document.RootElement.TryGetProperty("ok", out var ok) &&
                        ok.ValueKind is JsonValueKind.True)
                    {
                        return document;
                    }
                    document.Dispose();
                }
            }
            catch (Exception error) when (
                error is HttpRequestException or TaskCanceledException)
            {
            }
            await Task.Delay(100);
        }

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        Assert.Fail(
            "真实 C# Active Host 未在截止时间前就绪。\n" +
            await FailureOutputAsync(outputTask, errorTask));
        throw new InvalidOperationException("unreachable");
    }

    private static async Task WriteBoundCheckpointAsync(
        string dataDirectory,
        string operationId,
        string instanceName,
        int processId)
    {
        var checkpoint = new
        {
            schemaVersion = 1,
            operationId,
            updatedAt = DateTimeOffset.UtcNow,
            stage = "DotNetStartRequested",
            requiresRollback = false,
            failureReason = "None",
            expectedNode = new
            {
                processId = 81001,
                hostKind = "node",
                managementApiVersion = 1,
                ownershipMode = "active",
                activeOwner = true,
                instanceName = "production",
            },
            expectedDotNetInstanceName = instanceName,
            dotNetProcessId = processId,
            nodeRollbackProcessId = 0,
        };
        var target = Path.Combine(
            dataDirectory,
            BridgeActiveStartupGate.CheckpointFileName);
        var temporary = $"{target}.test-{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(checkpoint) + "\n");
        File.Move(temporary, target);
    }

    private static int ReservePort(int? excluding = null)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (port != excluding)
            {
                return port;
            }
        }
        throw new InvalidOperationException("无法分配两个不同的隔离回环端口。");
    }

    private static async Task RejectProxyConnectionsAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken);
                client.Close();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private static void AddArgument(
        ProcessStartInfo startInfo,
        string name,
        string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private static async Task<string> FailureOutputAsync(
        Task<string> outputTask,
        Task<string> errorTask) =>
        $"stdout:\n{await outputTask}\nstderr:\n{await errorTask}";

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
