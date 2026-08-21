using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiCliFeishu.Bridge.Adapters.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishuControl;

[TestClass]
public sealed class BridgeSettingsPayloadTests
{
    [DataTestMethod]
    [DataRow(BridgeAutoApproveModes.Relaxed, true)]
    [DataRow(BridgeAutoApproveModes.Strict, true)]
    [DataRow(BridgeAutoApproveModes.Off, false)]
    public void SelectedTierReachesTheHost(string mode, bool autoApprove)
    {
        var payload = Serialize(new BridgeSettings
        {
            WorkspaceRoot = @"K:\work",
            AutoApproveMode = mode,
            AutoApprove = autoApprove,
        });

        Assert.AreEqual(mode, payload.GetProperty("autoApproveMode").GetString());
        Assert.AreEqual(autoApprove, payload.GetProperty("autoApprove").GetBoolean());
    }

    [TestMethod]
    // The Host derives the tier from the boolean when the tier is missing, and the boolean
    // cannot express relaxed, so an unset tier must still be sent as a concrete value.
    public void AnUnsetTierIsResolvedRatherThanSentAsNull()
    {
        var payload = Serialize(new BridgeSettings
        {
            WorkspaceRoot = @"K:\work",
            AutoApproveMode = null,
            AutoApprove = true,
        });

        Assert.AreEqual(
            BridgeAutoApproveModes.Strict,
            payload.GetProperty("autoApproveMode").GetString());
    }

    [TestMethod]
    // Every property of BridgeSettings is a writable setting, and the Host treats an
    // absent one as "leave it unchanged". A setting the payload forgets is therefore lost
    // without any error, so the mirror has to stay complete.
    public void PayloadCarriesEverySetting()
    {
        var expected = typeof(BridgeSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property =>
                property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ??
                property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var actual = Serialize(new BridgeSettings { WorkspaceRoot = @"K:\work" })
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(expected, actual);
    }

    private static JsonElement Serialize(BridgeSettings settings) =>
        JsonSerializer.SerializeToElement(BridgeSettingsPayload.Create(settings));
}
