namespace AiCliFeishu.Bridge.Adapters.Storage;

public enum NodeStoreFileKind
{
    Bindings,
    Sessions,
    Routes,
    Approvals,
    Settings,
    ControlToken,
}

public sealed record NodeStoreFile(NodeStoreFileKind Kind, string FileName)
{
    public static NodeStoreFile Bindings { get; } = new(NodeStoreFileKind.Bindings, "bindings.json");
    public static NodeStoreFile Sessions { get; } = new(NodeStoreFileKind.Sessions, "sessions.json");
    public static NodeStoreFile Routes { get; } = new(NodeStoreFileKind.Routes, "message-routes.json");
    public static NodeStoreFile Approvals { get; } = new(NodeStoreFileKind.Approvals, "approvals.json");
    public static NodeStoreFile Settings { get; } = new(NodeStoreFileKind.Settings, "settings.json");
    public static NodeStoreFile ControlToken { get; } = new(NodeStoreFileKind.ControlToken, "control-token.json");

    public static IReadOnlyList<NodeStoreFile> All { get; } =
        [Bindings, Sessions, Routes, Approvals, Settings, ControlToken];
}
