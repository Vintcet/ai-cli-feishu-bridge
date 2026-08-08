using AiCliFeishu.Bridge.Adapters.Feishu;

namespace AiCliFeishu.Bridge.FeishuAdapter.Tests;

[TestClass]
public sealed class FeishuAliasCommandParserTests
{
    [TestMethod]
    public void ParsesShortIdSetQueryAndClearCommands()
    {
        var set = FeishuAliasCommandParser.Parse("别名 #A1B2C3D4 主项目");
        var query = FeishuAliasCommandParser.Parse("别名 #a1b2c3d4");
        var clear = FeishuAliasCommandParser.Parse("别名 #a1b2c3d4 清除");

        Assert.AreEqual(FeishuAliasTargetKind.ShortId, set!.TargetKind);
        Assert.AreEqual("a1b2c3d4", set.Target);
        Assert.AreEqual("主项目", set.Alias);
        Assert.IsNull(query!.Alias);
        Assert.AreEqual("清除", clear!.Alias);
    }

    [TestMethod]
    public void ParsesOldAliasRenameAndListsWithoutTarget()
    {
        var rename = FeishuAliasCommandParser.Parse("别名 @旧名称 新名称");
        var list = FeishuAliasCommandParser.Parse("别名");

        Assert.AreEqual(FeishuAliasTargetKind.Alias, rename!.TargetKind);
        Assert.AreEqual("旧名称", rename.Target);
        Assert.AreEqual("新名称", rename.Alias);
        Assert.IsNull(list!.TargetKind);
        Assert.IsTrue(FeishuAliasCommandParser.IsListCommand("/别名"));
    }

    [TestMethod]
    [DataRow("别名 #abc 设置")]
    [DataRow("别名 #123 过短")]
    [DataRow("别名 @bad#target 新名称")]
    [DataRow("别名 ???")]
    public void RejectsMalformedAliasCommands(string text)
    {
        Assert.IsNull(FeishuAliasCommandParser.Parse(text));
    }

    [TestMethod]
    public void UsageDocumentsAllSupportedForms()
    {
        var usage = FeishuAliasCommandParser.Usage();

        StringAssert.Contains(usage, "别名 #短ID 名称");
        StringAssert.Contains(usage, "别名 @旧别名 新名称");
        StringAssert.Contains(usage, "1–20");
    }
}
