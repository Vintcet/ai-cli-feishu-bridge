using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.OpenCode;

namespace AiCliFeishu.Bridge.Host;

internal sealed class OpenCodeAutoDiscoverySubsystem :
    IBridgeHostSubsystem,
    IBridgeHostSubsystemHealth,
    IBridgeBackgroundSubsystem
{
    private readonly BridgeHostOptions options;
    private readonly IBridgeOpenCodeEndpointRegistrationDirectory directory;
    private readonly IBridgeOpenCodeEventStreamOwner source;
    private readonly HttpClient httpClient;
    private readonly bool enabled;
    private readonly TimeSpan interval;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<int, int> misses = [];
    private Task? loop;
    private bool started;

    public OpenCodeAutoDiscoverySubsystem(
        BridgeHostOptions options,
        IBridgeOpenCodeEndpointRegistrationDirectory directory,
        IBridgeOpenCodeEventStreamOwner source)
    {
        this.options = options;
        this.directory = directory;
        this.source = source;
        enabled = !string.Equals(
            BridgeLocalConfiguration.Read(options, "OPENCODE_AUTO_DISCOVER"),
            "0",
            StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                BridgeLocalConfiguration.Read(options, "OPENCODE_AUTO_DISCOVER"),
                "false",
                StringComparison.OrdinalIgnoreCase);
        var configured = BridgeLocalConfiguration.Read(
            options,
            "OPENCODE_AUTO_DISCOVER_INTERVAL_MS");
        interval = TimeSpan.FromMilliseconds(
            int.TryParse(configured, out var milliseconds) && milliseconds >= 1_000
                ? milliseconds
                : 20_000);
        httpClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(2),
        })
        {
            Timeout = TimeSpan.FromSeconds(3),
        };
    }

    public string Name => "opencode-auto-discovery";

    public Task? Completion => loop;

    public BridgeComponentHealth ComponentHealth =>
        new(Name, "ready", enabled ? null : "configured-off");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!enabled)
        {
            started = true;
            return Task.CompletedTask;
        }
        if (started)
        {
            return Task.CompletedTask;
        }
        started = true;
        loop = RunAsync(lifetime.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        lifetime.Cancel();
        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
        httpClient.Dispose();
        started = false;
    }

    internal Task RunPassAsync(CancellationToken cancellationToken = default) =>
        ScanAsync(cancellationToken);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // A failed discovery pass is retried on the next interval.
            }
            await Task.Delay(interval, cancellationToken);
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        var known = directory.ListRegistrations()
            .ToDictionary(item => item.Port);
        var listeners = IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Where(endpoint => endpoint.Address.Equals(IPAddress.Loopback) ||
                endpoint.Address.Equals(IPAddress.Any))
            .Select(endpoint => endpoint.Port)
            .Distinct()
            .ToArray();
        foreach (var port in listeners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (known.ContainsKey(port))
            {
                continue;
            }
            var endpoint = new OpenCodeEndpoint(
                new Uri($"http://127.0.0.1:{port}/"),
                null,
                true);
            if (!await source.ProbeHealthAsync(endpoint, cancellationToken))
            {
                continue;
            }
            var cwd = await CurrentDirectoryAsync(port, cancellationToken);
            if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathFullyQualified(cwd))
            {
                continue;
            }
            directory.TryRegisterAvailable(port, cwd);
        }

        foreach (var registration in directory.ListRegistrations())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var healthy = await source.ProbeHealthAsync(
                registration.Endpoint,
                cancellationToken);
            if (healthy)
            {
                misses.Remove(registration.Port);
                continue;
            }
            var count = misses.GetValueOrDefault(registration.Port) + 1;
            if (count >= 3)
            {
                misses.Remove(registration.Port);
                directory.Unregister(registration.Port, registration.Generation);
            }
            else
            {
                misses[registration.Port] = count;
            }
        }
    }

    private async Task<string?> CurrentDirectoryAsync(
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                $"http://127.0.0.1:{port}/path",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(
                cancellationToken);
            return body?.GetValueOrDefault("directory") ??
                body?.GetValueOrDefault("worktree");
        }
        catch (Exception error) when (
            error is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }
}
