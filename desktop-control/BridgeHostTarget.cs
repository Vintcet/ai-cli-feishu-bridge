using System.Diagnostics;
using System.Globalization;

namespace AiCliFeishuControl;

internal sealed record BridgeHostTarget(
    string HostKind,
    int ManagementApiVersion,
    int Port,
    string OwnershipMode,
    bool ActiveOwner,
    string InstanceName)
{
    public const int CurrentManagementApiVersion = 1;
    public const string DotNetProductionInstanceName = "production-dotnet";

    public bool IsProduction => true;

    public string DisplayName => "C# 生产 Host";

    public static BridgeHostTarget DotNetProduction(
        int port,
        string instanceName = DotNetProductionInstanceName) =>
        new(
            "dotnet",
            CurrentManagementApiVersion,
            port,
            "active",
            true,
            instanceName);

    public static BridgeHostTarget FromConfiguration(string? configured, int productionPort)
    {
        configured = configured?.Trim();
        if (string.IsNullOrEmpty(configured) ||
            configured.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            configured.Equals("dotnet-production", StringComparison.OrdinalIgnoreCase))
        {
            return DotNetProduction(productionPort);
        }
        throw new InvalidOperationException(
            "AI_CLI_FEISHU_BRIDGE_HOST 只接受 dotnet；未设置时使用 C# 生产 Host。");
    }

    public bool Matches(BridgeStatus status) =>
        status.ProcessId > 0 &&
        string.Equals(status.HostKind, HostKind, StringComparison.Ordinal) &&
        status.ManagementApiVersion == ManagementApiVersion &&
        string.Equals(status.OwnershipMode, OwnershipMode, StringComparison.Ordinal) &&
        status.ActiveOwner == ActiveOwner &&
        string.Equals(status.InstanceName, InstanceName, StringComparison.Ordinal);

    public ProcessStartInfo CreateStartInfo(string bridgeRoot, string applicationDirectory)
    {
        var executable = ResolveDotNetHostExecutable(bridgeRoot, applicationDirectory);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = bridgeRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in new[]
                 {
                     "--data-directory", Path.Combine(bridgeRoot, "data"),
                     "--listen", "127.0.0.1",
                     "--port", Port.ToString(CultureInfo.InvariantCulture),
                     "--ownership", OwnershipMode,
                     "--instance", InstanceName,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        return startInfo;
    }

    private static string ResolveDotNetHostExecutable(
        string bridgeRoot,
        string applicationDirectory)
    {
        var configured = Environment.GetEnvironmentVariable("AI_CLI_FEISHU_DOTNET_HOST_PATH");
        var candidates = new[]
        {
            configured,
            Path.Combine(applicationDirectory, "AiCliFeishuBridgeHost.exe"),
            Path.Combine(bridgeRoot, "AiCliFeishuBridgeHost.exe"),
            Path.Combine(
                bridgeRoot,
                "bridge-dotnet",
                "src",
                "AiCliFeishu.Bridge.Host",
                "bin",
                "Release",
                "net8.0",
                "win-x64",
                "publish-sidecar",
                "AiCliFeishuBridgeHost.exe"),
            Path.Combine(
                bridgeRoot,
                "bridge-dotnet",
                "src",
                "AiCliFeishu.Bridge.Host",
                "bin",
                "Release",
                "net8.0",
                "AiCliFeishuBridgeHost.exe"),
        };
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        throw new FileNotFoundException(
            "找不到 C# Bridge Host。请先构建 AiCliFeishu.Bridge.Host，或设置 AI_CLI_FEISHU_DOTNET_HOST_PATH。");
    }
}
