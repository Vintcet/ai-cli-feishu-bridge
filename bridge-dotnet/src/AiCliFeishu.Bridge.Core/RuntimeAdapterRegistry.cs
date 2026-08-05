using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Core;

public sealed class RuntimeAdapterRegistry
{
    private readonly Dictionary<string, IRuntimeAdapter> adapters = new(
        StringComparer.Ordinal);

    public void Register(IRuntimeAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (!RuntimeNames.All.Contains(adapter.Runtime))
        {
            throw new ArgumentException(
                $"不支持的运行时 {adapter.Runtime}。",
                nameof(adapter));
        }
        if (!adapters.TryAdd(adapter.Runtime, adapter))
        {
            throw new InvalidOperationException(
                $"运行时 {adapter.Runtime} 已注册 Adapter。");
        }
    }

    public IRuntimeAdapter ForRuntime(string runtime)
    {
        if (!adapters.TryGetValue(runtime, out var adapter))
        {
            throw new KeyNotFoundException(
                $"运行时 {runtime} 未注册 Adapter。");
        }
        return adapter;
    }

    public IRuntimeAdapter RequireCapabilities(
        string runtime,
        IEnumerable<RuntimeCapability> capabilities)
    {
        var adapter = ForRuntime(runtime);
        var missing = capabilities
            .Where(capability => !adapter.Capabilities.Contains(capability))
            .Distinct()
            .ToArray();
        if (missing.Length > 0)
        {
            throw new NotSupportedException(
                $"运行时 {runtime} 不支持能力 {string.Join(", ", missing)}。");
        }
        return adapter;
    }
}
