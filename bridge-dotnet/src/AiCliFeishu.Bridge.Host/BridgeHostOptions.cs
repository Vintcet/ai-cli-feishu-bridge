using System.Net;

namespace AiCliFeishu.Bridge.Host;

public enum BridgeOwnershipMode
{
    Passive,
    Active,
}

public sealed record BridgeHostOptions(
    string DataDirectory,
    IPAddress ListenAddress,
    int Port,
    BridgeOwnershipMode OwnershipMode,
    string InstanceName)
{
    public const int DefaultPassivePort = 8876;

    public static BridgeHostOptions Passive(string dataDirectory, int port = DefaultPassivePort) =>
        new(
            Path.GetFullPath(dataDirectory),
            IPAddress.Loopback,
            port,
            BridgeOwnershipMode.Passive,
            "default");

    public BridgeHostOptions Validate()
    {
        if (string.IsNullOrWhiteSpace(DataDirectory))
        {
            throw new InvalidOperationException("Bridge Host 数据目录不能为空。");
        }
        if (!IPAddress.IsLoopback(ListenAddress))
        {
            throw new InvalidOperationException("Bridge Host 控制 API 只能监听本机回环地址。");
        }
        if (Port is < 0 or > 65_535)
        {
            throw new InvalidOperationException("Bridge Host 端口必须在 0 到 65535 之间。");
        }
        if (string.IsNullOrWhiteSpace(InstanceName) ||
            InstanceName.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new InvalidOperationException("Bridge Host 实例名只能包含字母、数字、连字符和下划线。");
        }
        if (OwnershipMode is BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException(
                "C# Bridge Host 的 Active Owner 切换尚未启用；Node 必须继续作为唯一生产执行者。");
        }
        return this with { DataDirectory = Path.GetFullPath(DataDirectory) };
    }

    public static BridgeHostOptions Parse(string[] args, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(args);
        var dataDirectory = Path.Combine(contentRoot, "data");
        var address = IPAddress.Loopback;
        var port = DefaultPassivePort;
        var ownership = BridgeOwnershipMode.Passive;
        var instanceName = "default";

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            string NextValue()
            {
                if (++index >= args.Length)
                {
                    throw new InvalidOperationException($"参数 {argument} 缺少值。");
                }
                return args[index];
            }

            switch (argument)
            {
                case "--data-directory":
                    dataDirectory = NextValue();
                    break;
                case "--listen":
                    if (!IPAddress.TryParse(NextValue(), out address))
                    {
                        throw new InvalidOperationException("--listen 必须是有效 IP 地址。");
                    }
                    break;
                case "--port":
                    if (!int.TryParse(NextValue(), out port))
                    {
                        throw new InvalidOperationException("--port 必须是整数。");
                    }
                    break;
                case "--instance":
                    instanceName = NextValue();
                    break;
                case "--ownership":
                    ownership = NextValue().ToLowerInvariant() switch
                    {
                        "passive" => BridgeOwnershipMode.Passive,
                        "active" => BridgeOwnershipMode.Active,
                        _ => throw new InvalidOperationException(
                            "--ownership 只接受 passive 或 active。"),
                    };
                    break;
                default:
                    throw new InvalidOperationException($"未知参数 {argument}。");
            }
        }

        return new BridgeHostOptions(
            dataDirectory,
            address,
            port,
            ownership,
            instanceName).Validate();
    }
}
