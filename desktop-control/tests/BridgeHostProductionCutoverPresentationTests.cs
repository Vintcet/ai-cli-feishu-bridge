using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishuControl.Tests;

[TestClass]
public sealed class BridgeHostProductionCutoverPresentationTests
{
    [DataTestMethod]
    [DataRow(true, false, true, false, false, true, true)]
    [DataRow(true, false, false, false, false, true, false)]
    [DataRow(true, false, true, true, false, true, false)]
    [DataRow(true, false, true, false, true, true, false)]
    [DataRow(true, true, true, false, false, false, false)]
    [DataRow(false, false, true, false, false, false, false)]
    public void CutoverButtonRequiresOnlineUnblockedNodeProduction(
        bool isProductionTarget,
        bool isDotNetProductionTarget,
        bool hostOnline,
        bool operating,
        bool ownershipBlocked,
        bool expectedVisible,
        bool expectedEnabled)
    {
        var state = BridgeHostProductionCutoverPresentation.GetButtonState(
            isProductionTarget,
            isDotNetProductionTarget,
            hostOnline,
            operating,
            ownershipBlocked);

        Assert.AreEqual(expectedVisible, state.Visible);
        Assert.AreEqual(expectedEnabled, state.Enabled);
    }
}
