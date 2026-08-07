using System.Text.RegularExpressions;

namespace AiCliFeishu.Bridge.Core;

public static partial class RuntimeErrorClassifier
{
    public static bool IsRetryable(string? message, string? errorCode = null) =>
        (!string.IsNullOrWhiteSpace(errorCode) && RetryableCode().IsMatch(errorCode)) ||
        (!string.IsNullOrWhiteSpace(message) && RetryableMessage().IsMatch(message));

    [GeneratedRegex(
        "(?:internal.server|server.error|bad.gateway|gateway.timeout|rate.limit|" +
        "overload|high.demand|temporar|timeout|(?:^|[_-])" +
        "(?:408|409|429|500|502|503|504)(?:$|[_-]))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RetryableCode();

    [GeneratedRegex(
        "(?:\\b(?:400|408|409|429|500|502|503|504)\\b|too many requests|" +
        "rate.?limit|busy|overload|high demand|temporar(?:y|ily)|" +
        "service unavailable|timeout|timed out|连接超时|服务繁忙|请求过多|暂时不可用)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RetryableMessage();
}
