namespace AiCliFeishu.Bridge.Adapters.Storage;

public enum BridgeStoreFileKind
{
    Bindings,
    Sessions,
    Routes,
    Approvals,
    Settings,
    ControlToken,
}

public sealed record BridgeStoreFile(BridgeStoreFileKind Kind, string FileName)
{
    public static BridgeStoreFile Bindings { get; } = new(BridgeStoreFileKind.Bindings, "bindings.json");
    public static BridgeStoreFile Sessions { get; } = new(BridgeStoreFileKind.Sessions, "sessions.json");
    public static BridgeStoreFile Routes { get; } = new(BridgeStoreFileKind.Routes, "message-routes.json");
    public static BridgeStoreFile Approvals { get; } = new(BridgeStoreFileKind.Approvals, "approvals.json");
    public static BridgeStoreFile Settings { get; } = new(BridgeStoreFileKind.Settings, "settings.json");
    public static BridgeStoreFile ControlToken { get; } = new(BridgeStoreFileKind.ControlToken, "control-token.json");

    public static IReadOnlyList<BridgeStoreFile> All { get; } =
        [Bindings, Sessions, Routes, Approvals, Settings, ControlToken];
}
