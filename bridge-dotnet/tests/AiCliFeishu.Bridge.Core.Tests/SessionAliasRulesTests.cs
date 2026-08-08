using AiCliFeishu.Bridge.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishu.Bridge.Core.Tests;

[TestClass]
public sealed class SessionAliasRulesTests
{
    [TestMethod]
    public void NormalizeTrimsAndUsesCanonicalUnicodeForm()
    {
        var decomposed = " e\u0301 ";

        var normalized = SessionAliasRules.Normalize(decomposed);

        Assert.AreEqual("é", normalized);
        Assert.AreEqual("é", SessionAliasRules.Key(decomposed));
    }

    [TestMethod]
    [DataRow("项目_1")]
    [DataRow("中文-会话")]
    [DataRow("Alpha-2")]
    public void AcceptsDocumentedAliasCharacters(string alias)
    {
        Assert.IsNull(SessionAliasRules.ValidationError(alias));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("two words")]
    [DataRow("bad/name")]
    [DataRow("bad.@name")]
    public void RejectsEmptyWhitespaceAndUnsupportedCharacters(string alias)
    {
        Assert.IsNotNull(SessionAliasRules.ValidationError(alias));
    }

    [TestMethod]
    public void CountsUnicodeScalarsRatherThanUtf16CodeUnits()
    {
        var twenty = string.Concat(Enumerable.Repeat("界", 20));
        var twentyOne = string.Concat(Enumerable.Repeat("界", 21));

        Assert.IsNull(SessionAliasRules.ValidationError(twenty));
        StringAssert.Contains(
            SessionAliasRules.ValidationError(twentyOne)!,
            "20");
    }

    [TestMethod]
    public void LatinCaseUsesTheSameReservationKey()
    {
        Assert.AreEqual(
            SessionAliasRules.Key("Alpha"),
            SessionAliasRules.Key(" alpha "));
    }
}
