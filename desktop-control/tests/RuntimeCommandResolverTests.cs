using AiCliFeishu.Bridge.Adapters.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishuControl;

[TestClass]
public sealed class RuntimeCommandResolverTests
{
    private static readonly RuntimeCommandEnvironment Environment = new(
        SearchPath: @"C:\Windows\system32;C:\Tools\bin",
        PathExtensions: ".COM;.EXE;.BAT;.CMD",
        UserProfile: @"C:\Users\tester",
        ApplicationData: @"C:\Users\tester\AppData\Roaming",
        LocalApplicationData: @"C:\Users\tester\AppData\Local");

    [TestMethod]
    public void EachRuntimeExposesACommandOverrideVariable()
    {
        Assert.AreEqual(
            "CODEX_COMMAND",
            RuntimeCommandResolver.OverrideVariableName(RuntimeCatalog.Codex));
        Assert.AreEqual(
            "CLAUDE_COMMAND",
            RuntimeCommandResolver.OverrideVariableName(RuntimeCatalog.ClaudeCode));
        Assert.AreEqual(
            "OPENCODE_COMMAND",
            RuntimeCommandResolver.OverrideVariableName(RuntimeCatalog.OpenCode));
    }

    [TestMethod]
    public void PowerShellShimsAreProbedAlongsideExecutables()
    {
        var extensions = RuntimeCommandResolver.Extensions(".COM;.EXE;.BAT;.CMD");

        CollectionAssert.AreEqual(
            new[] { ".exe", ".cmd", ".bat", ".ps1", ".com" },
            extensions.ToArray());
    }

    [TestMethod]
    public void ExtensionsToleratePathExtEntriesWithoutLeadingDots()
    {
        var extensions = RuntimeCommandResolver.Extensions("EXE; vbs ;\".wsf\"");

        CollectionAssert.AreEqual(
            new[] { ".exe", ".cmd", ".bat", ".ps1", ".vbs", ".wsf" },
            extensions.ToArray());
    }

    [TestMethod]
    public void ExtensionsStayUsableWhenPathExtIsMissing()
    {
        CollectionAssert.AreEqual(
            new[] { ".exe", ".cmd", ".bat", ".ps1" },
            RuntimeCommandResolver.Extensions(null).ToArray());
    }

    [TestMethod]
    public void APowerShellShimOnThePathIsResolved()
    {
        var shim = @"C:\Tools\bin\claude.ps1";

        var resolved = RuntimeCommandResolver.Resolve(
            RuntimeCatalog.ClaudeCode,
            Environment,
            candidate => string.Equals(candidate, shim, StringComparison.OrdinalIgnoreCase));

        Assert.AreEqual(shim, resolved);
    }

    [TestMethod]
    public void ABatchShimOnThePathIsResolved()
    {
        var shim = @"C:\Tools\bin\claude.bat";

        var resolved = RuntimeCommandResolver.Resolve(
            RuntimeCatalog.ClaudeCode,
            Environment,
            candidate => string.Equals(candidate, shim, StringComparison.OrdinalIgnoreCase));

        Assert.AreEqual(shim, resolved);
    }

    [TestMethod]
    public void ANativeExecutableWinsOverAShimInTheSameDirectory()
    {
        var resolved = RuntimeCommandResolver.Resolve(
            RuntimeCatalog.ClaudeCode,
            Environment,
            candidate => candidate.StartsWith(
                @"C:\Tools\bin\claude.",
                StringComparison.OrdinalIgnoreCase));

        Assert.AreEqual(@"C:\Tools\bin\claude.exe", resolved);
    }

    [TestMethod]
    public void TheNativeInstallerLocationIsProbed()
    {
        var installed = @"C:\Users\tester\.local\bin\claude.exe";

        var resolved = RuntimeCommandResolver.Resolve(
            RuntimeCatalog.ClaudeCode,
            Environment,
            candidate => string.Equals(
                candidate,
                installed,
                StringComparison.OrdinalIgnoreCase));

        Assert.AreEqual(installed, resolved);
    }

    [TestMethod]
    public void TheNpmShimLocationIsProbed()
    {
        var installed = @"C:\Users\tester\AppData\Roaming\npm\claude.cmd";

        var resolved = RuntimeCommandResolver.Resolve(
            RuntimeCatalog.ClaudeCode,
            Environment,
            candidate => string.Equals(
                candidate,
                installed,
                StringComparison.OrdinalIgnoreCase));

        Assert.AreEqual(installed, resolved);
    }

    [TestMethod]
    public void AMissingCommandResolvesToNull()
    {
        Assert.IsNull(RuntimeCommandResolver.Resolve(
            RuntimeCatalog.ClaudeCode,
            Environment,
            _ => false));
    }

    [TestMethod]
    public void PathEntriesAreProbedBeforeUserInstallDirectories()
    {
        var candidates = RuntimeCommandResolver.Candidates(
            RuntimeCatalog.ClaudeCode,
            Environment);

        var pathIndex = candidates.ToList().FindIndex(candidate =>
            candidate.Equals(@"C:\Tools\bin\claude.exe", StringComparison.OrdinalIgnoreCase));
        var localIndex = candidates.ToList().FindIndex(candidate =>
            candidate.Equals(
                @"C:\Users\tester\.local\bin\claude.exe",
                StringComparison.OrdinalIgnoreCase));

        Assert.IsTrue(pathIndex >= 0, "PATH candidate missing.");
        Assert.IsTrue(localIndex >= 0, "User install candidate missing.");
        Assert.IsTrue(pathIndex < localIndex);
    }

    [TestMethod]
    public void MalformedPathEntriesAreSkippedInsteadOfThrowing()
    {
        var environment = Environment with
        {
            SearchPath = "C:\\Tools\\bin;;\"C:\\Quoted\\bin\";C:\\Bad\0Entry",
        };

        var candidates = RuntimeCommandResolver.Candidates(
            RuntimeCatalog.ClaudeCode,
            environment);

        Assert.IsTrue(candidates.Any(candidate =>
            candidate.Equals(
                @"C:\Quoted\bin\claude.exe",
                StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(candidates.Any(candidate => candidate.Contains('\0')));
    }

    [TestMethod]
    public void DuplicatePathEntriesAreProbedOnce()
    {
        var environment = Environment with
        {
            SearchPath = @"C:\Tools\bin;C:\Tools\bin",
        };

        var candidates = RuntimeCommandResolver.Candidates(
            RuntimeCatalog.ClaudeCode,
            environment);

        Assert.AreEqual(
            1,
            candidates.Count(candidate => candidate.Equals(
                @"C:\Tools\bin\claude.exe",
                StringComparison.OrdinalIgnoreCase)));
    }
}
