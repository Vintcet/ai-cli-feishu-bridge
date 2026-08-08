using System.Diagnostics;
using System.Globalization;

namespace AiCliFeishuControl;

internal enum BridgeHostMode
{
    NodeProduction,
    DotNetProduction,
    DotNetShadow,
}

internal sealed record BridgeHostTarget(
    BridgeHostMode Mode,
    string HostKind,
    int ManagementApiVersion,
    int Port,
    string OwnershipMode,
    bool ActiveOwner,
    string InstanceName)
{
    public const int CurrentManagementApiVersion = 1;
    public const int DotNetShadowPort = 8876;
    public const string DotNetShadowInstanceName = "desktop-shadow";
    public const string DotNetProductionInstanceName = "production-dotnet";

    public bool IsProduction => Mode is not BridgeHostMode.DotNetShadow;

    public bool UsesNodeRuntime => Mode is BridgeHostMode.NodeProduction;

    public string DisplayName => Mode switch
    {
        BridgeHostMode.NodeProduction => "Node 生产 Host",
        BridgeHostMode.DotNetProduction => "C# 生产 Host",
        _ => "C# Shadow Host",
    };

    public static BridgeHostTarget NodeProduction(int port) =>
        new(
            BridgeHostMode.NodeProduction,
            "node",
            CurrentManagementApiVersion,
            port,
            "active",
            true,
            "production");

    public static BridgeHostTarget DotNetShadow() =>
        new(
            BridgeHostMode.DotNetShadow,
            "dotnet",
            CurrentManagementApiVersion,
            DotNetShadowPort,
            "passive",
            false,
            DotNetShadowInstanceName);

    public static BridgeHostTarget DotNetProduction(
        int port,
        string instanceName = DotNetProductionInstanceName) =>
        new(
            BridgeHostMode.DotNetProduction,
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
            configured.Equals("node", StringComparison.OrdinalIgnoreCase))
        {
            return NodeProduction(productionPort);
        }
        if (configured.Equals("dotnet-shadow", StringComparison.OrdinalIgnoreCase))
        {
            return DotNetShadow();
        }
        throw new InvalidOperationException(
            "AI_CLI_FEISHU_BRIDGE_HOST 只接受 node 或 dotnet-shadow；未设置时使用 Node 生产 Host。");
    }

    public bool Matches(BridgeStatus status) =>
        status.ProcessId > 0 &&
        string.Equals(status.HostKind, HostKind, StringComparison.Ordinal) &&
        status.ManagementApiVersion == ManagementApiVersion &&
        string.Equals(status.OwnershipMode, OwnershipMode, StringComparison.Ordinal) &&
        status.ActiveOwner == ActiveOwner &&
        (Mode is BridgeHostMode.NodeProduction ||
            string.Equals(status.InstanceName, InstanceName, StringComparison.Ordinal));

    public ProcessStartInfo CreateStartInfo(string bridgeRoot, string applicationDirectory)
    {
        if (UsesNodeRuntime)
        {
            var entryFile = Path.Combine(bridgeRoot, "dist", "index.js");
            if (!File.Exists(entryFile))
            {
                throw new FileNotFoundException(
                    "找不到已构建的桥接入口，请先运行 npm run build。",
                    entryFile);
            }
            var node = BaseStartInfo("node.exe", bridgeRoot);
            node.ArgumentList.Add(entryFile);
            node.Environment["BRIDGE_HTTP_PORT"] = Port.ToString(CultureInfo.InvariantCulture);
            return node;
        }

        var executable = ResolveDotNetHostExecutable(bridgeRoot, applicationDirectory);
        var dotnet = BaseStartInfo(executable, bridgeRoot);
        dotnet.ArgumentList.Add("--data-directory");
        dotnet.ArgumentList.Add(Path.Combine(bridgeRoot, "data"));
        dotnet.ArgumentList.Add("--listen");
        dotnet.ArgumentList.Add("127.0.0.1");
        dotnet.ArgumentList.Add("--port");
        dotnet.ArgumentList.Add(Port.ToString(CultureInfo.InvariantCulture));
        dotnet.ArgumentList.Add("--ownership");
        dotnet.ArgumentList.Add(OwnershipMode);
        dotnet.ArgumentList.Add("--instance");
        dotnet.ArgumentList.Add(InstanceName);
        dotnet.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        return dotnet;
    }

    private static ProcessStartInfo BaseStartInfo(string executable, string workingDirectory) =>
        new()
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

    private static string ResolveDotNetHostExecutable(
        string bridgeRoot,
        string applicationDirectory)
    {
        var configured = Environment.GetEnvironmentVariable("AI_CLI_FEISHU_DOTNET_HOST_PATH");
        var candidates = new[]
        {
            configured,
            Path.Combine(applicationDirectory, "AiCliFeishuBridgeHost.exe"),
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
