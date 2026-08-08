namespace AiCliFeishuControl;

internal sealed record BridgeHostProductionCutoverButtonState(
    bool Visible,
    bool Enabled);

internal static class BridgeHostProductionCutoverPresentation
{
    public static BridgeHostProductionCutoverButtonState GetButtonState(
        bool isProductionTarget,
        bool isDotNetProductionTarget,
        bool hostOnline,
        bool operating,
        bool ownershipBlocked)
    {
        var visible = isProductionTarget && !isDotNetProductionTarget;
        return new(
            visible,
            visible && hostOnline && !operating && !ownershipBlocked);
    }
}
