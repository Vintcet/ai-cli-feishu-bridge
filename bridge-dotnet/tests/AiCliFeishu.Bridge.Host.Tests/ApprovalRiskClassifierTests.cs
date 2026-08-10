namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ApprovalRiskClassifierTests
{
    private const string Workspace = @"K:\repo";

    [TestMethod]
    public void AllowsExplicitReadOnlyShellCommand()
    {
        var result = ApprovalRiskClassifier.Assess(
            "shell_command",
            "{\"command\":\"git status\"}",
            Workspace);

        Assert.AreEqual("low", result.Level);
    }

    [DataTestMethod]
    [DataRow("shell_command", "{\"command\":\"Get-Content K:\\\\repo\\\\README.md\"}")]
    [DataRow("shell_command", "{\"command\":\"rg needle .\"}")]
    [DataRow("read", "{\"path\":\"K:\\\\repo\\\\README.md\"}")]
    [DataRow("apply_patch", "{\"patch\":\"*** Begin Patch\\n*** Update File: K:\\\\repo\\\\README.md\\n@@\\n-old\\n+new\\n*** End Patch\"}")]
    public void AllowsWorkspaceBoundReadOnlyRequests(string tool, string preview)
    {
        var result = ApprovalRiskClassifier.Assess(tool, preview, Workspace);

        Assert.AreEqual("low", result.Level);
    }

    [DataTestMethod]
    [DataRow("unknown_tool", "{}")]
    [DataRow("shell_command", "not-json")]
    [DataRow("shell_command", "{\"command\":\"git reset --hard\"}")]
    [DataRow("shell_command", "{\"command\":\"curl https://example.com\"}")]
    [DataRow("shell_command", "{\"command\":\"Get-Content K:\\\\outside\\\\ordinary.txt\"}")]
    [DataRow("shell_command", "{\"command\":\"Get-Content Env:AI_CLI_FEISHU_CONTROL_TOKEN\"}")]
    [DataRow("shell_command", "{\"command\":\"Get-Content K:\\\\repo\\\\data\\\\control-token.json\"}")]
    [DataRow("shell_command", "{\"command\":\"Get-ChildItem HKLM:\\\\Software\"}")]
    [DataRow("shell_command", "{\"command\":\"Get-Content $env:AI_CLI_FEISHU_CONTROL_TOKEN\"}")]
    [DataRow("shell_command", "{\"command\":\"Get-Content ([System.IO.File]::ReadAllText('K:\\\\outside\\\\secret.txt'))\"}")]
    [DataRow("shell_command", "{\"command\":\"dotnet build ..\\\\outside\\\\malicious.csproj\"}")]
    [DataRow("shell_command", "{\"command\":\"dotnet build\"}")]
    [DataRow("shell_command", "{\"command\":\"npm run build\"}")]
    [DataRow("shell_command", "{\"command\":\"git diff --no-index K:\\\\repo\\\\a.txt K:\\\\outside\\\\b.txt\"}")]
    [DataRow("shell_command", "{\"command\":\"git diff --output=K:\\\\repo\\\\diff.txt\"}")]
    [DataRow("read", "{}")]
    [DataRow("apply_patch", "{\"patch\":\"*** Begin Patch\\n*** Update File: K:\\\\outside\\\\README.md\\n@@\\n-old\\n+new\\n*** End Patch\"}")]
    [DataRow("read", "{\"path\":\"K:\\\\outside\\\\secret.txt\"}")]
    [DataRow("read", "{\"path\":\"K:\\\\repo\\\\.env\"}")]
    public void FailsClosedForUnknownIncompleteOrDangerousRequests(
        string tool,
        string preview)
    {
        var result = ApprovalRiskClassifier.Assess(tool, preview, Workspace);

        Assert.AreEqual("high", result.Level);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Reason));
    }
}
