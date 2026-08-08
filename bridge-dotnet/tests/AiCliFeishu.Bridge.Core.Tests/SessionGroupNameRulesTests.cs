using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishu.Bridge.Core.Tests;

[TestClass]
public sealed class SessionGroupNameRulesTests
{
    [TestMethod]
    [DataRow(RuntimeNames.Codex, "Codex｜项目")]
    [DataRow(RuntimeNames.ClaudeCode, "Claude｜项目")]
    [DataRow(RuntimeNames.OpenCode, "OpenCode｜项目")]
    public void UsesTheNodeRuntimePrefixes(string runtime, string expected)
    {
        Assert.AreEqual(
            expected,
            SessionGroupNameRules.Build(runtime, null, "项目", "12345678"));
    }

    [TestMethod]
    public void AliasWinsAndOrdinalIsOnlyUsedAfterAliasIsCleared()
    {
        Assert.AreEqual(
            "Codex｜别名",
            SessionGroupNameRules.Build(
                RuntimeNames.Codex,
                "别名",
                "项目",
                "12345678",
                ordinal: 2));
        Assert.AreEqual(
            "Codex｜项目（2）",
            SessionGroupNameRules.Build(
                RuntimeNames.Codex,
                null,
                "项目",
                "12345678",
                ordinal: 2));
        Assert.AreEqual(
            "Codex｜12345678",
            SessionGroupNameRules.Build(
                RuntimeNames.Codex,
                null,
                null,
                "12345678",
                ordinal: 1));
    }

    [TestMethod]
    public void TruncatesToSixtyUtf16CodeUnitsWithoutSplittingEmoji()
    {
        var name = SessionGroupNameRules.Build(
            RuntimeNames.Codex,
            null,
            string.Concat(Enumerable.Repeat("😀", 80)),
            "short",
            ordinal: 2);

        Assert.IsTrue(name.Length <= SessionGroupNameRules.MaximumLength);
        Assert.AreEqual(59, name.Length);
        Assert.IsTrue(IsWellFormedUtf16(name));
        Assert.AreEqual("Codex｜", name[..6]);
        StringAssert.EndsWith(name, "（2）");
    }

    [TestMethod]
    public void FallsBackFromEmptyProjectToShortId()
    {
        Assert.AreEqual(
            "Claude｜short-id",
            SessionGroupNameRules.Build(
                RuntimeNames.ClaudeCode,
                "   ",
                "",
                "short-id"));
    }

    private static bool IsWellFormedUtf16(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsLowSurrogate(value[index]))
            {
                return false;
            }
            if (!char.IsHighSurrogate(value[index]))
            {
                continue;
            }
            if (++index >= value.Length || !char.IsLowSurrogate(value[index]))
            {
                return false;
            }
        }
        return true;
    }
}
