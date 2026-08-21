namespace AiCliFeishu.Bridge.Adapters.Storage;

public static class BridgeSettingsLimits
{
    public const int RetryMaxAttemptsMinimum = 1;
    public const int RetryMaxAttemptsMaximum = 999;
    public const int RetryMaxAttemptsDefault = 3;
}

public static class BridgeAutoApproveModes
{
    // Every approval waits for a person.
    public const string Off = "off";

    // Allowlist: only explicitly recognized read-only requests are approved.
    public const string Strict = "strict";

    // Denylist: everything inspectable and reversible is approved; irreversible
    // requests, workspace escapes, and anything that cannot be inspected still wait.
    public const string Relaxed = "relaxed";

    public static bool IsValid(string? value) =>
        value is Off or Strict or Relaxed;

    // `autoApproveMode` was added after `autoApprove`, so a store written by an older
    // build only carries the boolean and has to keep behaving the same.
    public static string Resolve(string? mode, bool? autoApprove) =>
        IsValid(mode)
            ? mode!
            : autoApprove == true
                ? Strict
                : Off;

    // Older builds only read `autoApprove`, so it is kept in sync. Downgrading from
    // relaxed then lands on strict rather than silently approving medium requests.
    public static bool ToLegacyAutoApprove(string mode) => mode != Off;
}
