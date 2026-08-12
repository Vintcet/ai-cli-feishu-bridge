namespace AiCliFeishuControl;

internal static class BridgeHookInstallCoordinator
{
    internal static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(20);

    public static async Task EnsureAllAsync(
        Func<string, TimeSpan, Task> installScript)
    {
        ArgumentNullException.ThrowIfNull(installScript);
        await installScript("install-hooks.ps1", ScriptTimeout);
        await installScript("install-claude-code-hooks.ps1", ScriptTimeout);
    }

    public static Task EnsureRuntimeAsync(
        RuntimeProfile runtime,
        Func<string, TimeSpan, Task> installScript)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(installScript);
        return string.Equals(
                runtime.Id,
                RuntimeCatalog.ClaudeCode.Id,
                StringComparison.Ordinal)
            ? installScript("install-claude-code-hooks.ps1", ScriptTimeout)
            : Task.CompletedTask;
    }
}
