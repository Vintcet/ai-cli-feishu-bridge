namespace AiCliFeishuControl;

internal enum RuntimeTransport
{
    ManagedTerminal,
    HttpEventStream,
}

internal sealed record RuntimeProfile(
    string Id,
    string DisplayName,
    string ShortName,
    string CommandName,
    RuntimeTransport Transport,
    string ArgumentsPlaceholder,
    string LaunchHint,
    string ResumeFlag,
    bool RequiresResolvedCommand = false,
    bool InjectBridgeArguments = false,
    bool QueueWithTab = false,
    string? LocalProgramDirectory = null)
{
    public bool UsesManagedTerminal => Transport == RuntimeTransport.ManagedTerminal;

    public string BuildResumeArguments(string sessionId) => $"{ResumeFlag} {sessionId}";

    public bool MatchesCommand(string value)
    {
        var fileName = Path.GetFileName(value);
        return fileName.Equals(CommandName, StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals($"{CommandName}.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals($"{CommandName}.cmd", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals($"{CommandName}.ps1", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class RuntimeCatalog
{
    public static readonly RuntimeProfile Codex = new(
        Id: "codex",
        DisplayName: "Codex",
        ShortName: "Codex",
        CommandName: "codex",
        Transport: RuntimeTransport.ManagedTerminal,
        ArgumentsPlaceholder: "例如：resume 019faef0-d0bb-7703-af82-17ee9b45397b",
        LaunchHint: "优先使用 Windows Terminal；管理员模式只影响这个 Codex 窗口。",
        ResumeFlag: "resume",
        InjectBridgeArguments: true,
        QueueWithTab: true);

    public static readonly RuntimeProfile ClaudeCode = new(
        Id: "claudecode",
        DisplayName: "Claude Code",
        ShortName: "Claude",
        CommandName: "claude",
        Transport: RuntimeTransport.ManagedTerminal,
        ArgumentsPlaceholder: "例如：--resume 019faef0-d0bb-7703-af82-17ee9b45397b",
        LaunchHint: "Claude Code CLI（claude）需已安装并可用；桥接服务会自动登记会话。",
        ResumeFlag: "--resume",
        RequiresResolvedCommand: true,
        LocalProgramDirectory: "Claude");

    public static readonly RuntimeProfile OpenCode = new(
        Id: "opencode",
        DisplayName: "opencode",
        ShortName: "opencode",
        CommandName: "opencode",
        Transport: RuntimeTransport.HttpEventStream,
        ArgumentsPlaceholder: "例如：-s 019faef0-d0bb-7703-af82-17ee9b45397b",
        LaunchHint: "opencode 需已安装并可用；桥接服务会保留本机端口并自动登记会话。",
        ResumeFlag: "-s",
        RequiresResolvedCommand: true,
        LocalProgramDirectory: "opencode");

    public static IReadOnlyList<RuntimeProfile> All { get; } =
        [Codex, ClaudeCode, OpenCode];

    public static RuntimeProfile FromId(string? runtimeId) =>
        All.FirstOrDefault(
            profile => profile.Id.Equals(runtimeId, StringComparison.OrdinalIgnoreCase)) ?? Codex;

    public static bool TryGet(string? runtimeId, out RuntimeProfile profile)
    {
        var match = All.FirstOrDefault(
            candidate => candidate.Id.Equals(runtimeId, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            profile = Codex;
            return false;
        }
        profile = match;
        return true;
    }
}
