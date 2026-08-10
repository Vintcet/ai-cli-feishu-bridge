using System.Diagnostics;
using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AiCliFeishuControl;

internal sealed partial class BridgeClient
{
    public async Task SetSessionAliasAsync(
        string sessionId,
        string? alias,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "sessions/alias")
        {
            Content = JsonContent.Create(new { sessionId, alias }),
        };
        request.Headers.Add(
            "X-AI-CLI-Feishu-Control-Token",
            ReadControlToken(BridgeRoot));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        AliasUpdateResult? result = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            result = await JsonSerializer.DeserializeAsync<AliasUpdateResult>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);
        }
        catch (JsonException)
        {
        }

        if (!response.IsSuccessStatusCode || result?.Ok != true)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result?.Error) ? "设置会话别名失败。" : result.Error);
        }
    }

    public async Task RetrySessionGroupAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "sessions/feishu-group/retry")
        {
            Content = JsonContent.Create(new { sessionId }),
        };
        request.Headers.Add(
            "X-AI-CLI-Feishu-Control-Token",
            ReadControlToken(BridgeRoot));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        SessionGroupRetryResult? result = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            result = await JsonSerializer.DeserializeAsync<SessionGroupRetryResult>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);
        }
        catch (JsonException)
        {
        }
        if (!response.IsSuccessStatusCode || result?.Ok != true)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result?.Error) ? "创建飞书会话群失败。" : result.Error);
        }
    }

    public async Task<ApprovalResolveResult> ResolveApprovalAsync(
        string requestId,
        string resolution,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId) ||
            resolution is not ("allow" or "deny"))
        {
            throw new InvalidOperationException("审批请求参数不正确。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "approvals/resolve")
        {
            Content = JsonContent.Create(new { requestId, resolution }),
        };
        request.Headers.Add(
            "X-AI-CLI-Feishu-Control-Token",
            ReadControlToken(BridgeRoot));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        ApprovalResolveResult? result = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            result = await JsonSerializer.DeserializeAsync<ApprovalResolveResult>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);
        }
        catch (JsonException)
        {
        }

        if (!response.IsSuccessStatusCode || result?.Ok != true)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result?.Error) ? "处理本机审批失败。" : result.Error);
        }
        return result;
    }

    public async Task HideSessionFromHistoryAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException("会话 ID 参数不正确。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "sessions/history/hide")
        {
            Content = JsonContent.Create(new { sessionId }),
        };
        request.Headers.Add(
            "X-AI-CLI-Feishu-Control-Token",
            ReadControlToken(BridgeRoot));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        HistoryHideResult? result = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            result = await JsonSerializer.DeserializeAsync<HistoryHideResult>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);
        }
        catch (JsonException)
        {
        }

        if (!response.IsSuccessStatusCode || result?.Ok != true)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result?.Error) ? "删除历史记录失败。" : result.Error);
        }
    }

    public async Task<RuntimeLaunchRequest?> ClaimRuntimeLaunchAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "runtime-launches/claim")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add(
            "X-AI-CLI-Feishu-Control-Token",
            ReadControlToken(BridgeRoot));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        RuntimeLaunchClaimResult? result = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            result = await JsonSerializer.DeserializeAsync<RuntimeLaunchClaimResult>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);
        }
        catch (JsonException)
        {
        }
        if (!response.IsSuccessStatusCode || result?.Ok != true)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result?.Error) ? "读取自动恢复请求失败。" : result.Error);
        }
        return result.Request;
    }

    public async Task CompleteRuntimeLaunchAsync(
        string requestId,
        bool success,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    "runtime-launches/complete")
                {
                    Content = JsonContent.Create(new { requestId, success, error }),
                };
                request.Headers.Add(ControlTokenHeader, ReadControlToken(BridgeRoot));
                using var response = await httpClient.SendAsync(request, cancellationToken);
                RuntimeLaunchCompleteResult? result = null;
                try
                {
                    await using var stream = await response.Content
                        .ReadAsStreamAsync(cancellationToken);
                    result = await JsonSerializer.DeserializeAsync<RuntimeLaunchCompleteResult>(
                        stream,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                        cancellationToken);
                }
                catch (JsonException)
                {
                }
                if (response.IsSuccessStatusCode && result?.Ok == true)
                {
                    return;
                }
                lastError = new InvalidOperationException(
                    string.IsNullOrWhiteSpace(result?.Error)
                        ? "提交自动恢复结果失败。"
                        : result.Error);
                var retryable = response.StatusCode is
                        System.Net.HttpStatusCode.Unauthorized or
                        System.Net.HttpStatusCode.RequestTimeout or
                        System.Net.HttpStatusCode.TooManyRequests ||
                    (int)response.StatusCode >= 500 ||
                    result is null;
                if (!retryable || attempt == 2)
                {
                    throw lastError;
                }
            }
            catch (Exception retryError) when (
                attempt < 2 &&
                !cancellationToken.IsCancellationRequested &&
                retryError is HttpRequestException or TaskCanceledException)
            {
                lastError = retryError;
            }
            AppLog.Warn(
                $"提交自动恢复结果失败，将以相同 requestId 重试（{attempt + 2}/3）：" +
                lastError?.Message);
            await Task.Delay(TimeSpan.FromMilliseconds(150 * (attempt + 1)), cancellationToken);
        }
        throw new InvalidOperationException("提交自动恢复结果失败。", lastError);
    }

    public async Task<BridgeSettings> UpdateSettingsAsync(
        BridgeSettings settings,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "settings")
        {
            Content = JsonContent.Create(new
            {
                workspaceRoot = settings.WorkspaceRoot,
                notifyActivity = settings.NotifyActivity,
                notifyUserPrompts = settings.NotifyUserPrompts,
                autoRetryErrors = settings.AutoRetryErrors,
                retryMaxAttempts = settings.RetryMaxAttempts,
                retryIntervalSeconds = settings.RetryIntervalSeconds,
                retryJitterSeconds = settings.RetryJitterSeconds,
                autoApprove = settings.AutoApprove,
                notifyAutoApprovals = settings.NotifyAutoApprovals,
            }),
        };
        request.Headers.Add(
            "X-AI-CLI-Feishu-Control-Token",
            ReadControlToken(BridgeRoot));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        SettingsUpdateResult? result = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            result = await JsonSerializer.DeserializeAsync<SettingsUpdateResult>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);
        }
        catch (JsonException)
        {
        }
        if (!response.IsSuccessStatusCode || result?.Ok != true)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result?.Error) ? "保存设置失败。" : result.Error);
        }
        return result.Settings;
    }

}
