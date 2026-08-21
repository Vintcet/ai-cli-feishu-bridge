using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishuControl;

// The settings request body mirrors BridgeSettings field by field, and the Host reads an
// absent field as "leave this setting unchanged" -- so a field the mirror forgets is
// dropped silently instead of failing. Kept out of BridgeClient so a test can hold the
// mirror against the model.
internal static class BridgeSettingsPayload
{
    internal static object Create(BridgeSettings settings) => new
    {
        workspaceRoot = settings.WorkspaceRoot,
        notifyActivity = settings.NotifyActivity,
        notifyUserPrompts = settings.NotifyUserPrompts,
        autoRetryErrors = settings.AutoRetryErrors,
        retryMaxAttempts = settings.RetryMaxAttempts,
        retryIntervalSeconds = settings.RetryIntervalSeconds,
        retryJitterSeconds = settings.RetryJitterSeconds,
        autoApprove = settings.AutoApprove,
        // Always sent, and never null: the Host derives the tier from the boolean when
        // this is missing, and the boolean cannot express relaxed.
        autoApproveMode = BridgeAutoApproveModes.Resolve(
            settings.AutoApproveMode,
            settings.AutoApprove),
        notifyAutoApprovals = settings.NotifyAutoApprovals,
    };
}
