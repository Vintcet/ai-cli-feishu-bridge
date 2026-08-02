import type { BridgeSettings } from "./domain.js";

export function isRetryableCodexError(value: string, errorCode?: string): boolean {
  if (
    errorCode &&
    /(?:internal.server|server.error|rate.limit|overload|high.demand|temporar|timeout)/i.test(
      errorCode,
    )
  ) {
    return true;
  }
  return /(?:\b(?:400|408|409|429|500|502|503|504)\b|too many requests|rate.?limit|busy|overload|high demand|temporar(?:y|ily)|service unavailable|timeout|timed out|连接超时|服务繁忙|请求过多|暂时不可用)/i.test(
    value,
  );
}

export function codexErrorFromMessage(
  value: string | null | undefined,
): string | undefined {
  const message = value?.trim();
  if (!message) {
    return undefined;
  }

  // A normal answer may discuss status codes or failures. Only accept an
  // explicitly error-shaped first line when no structured transcript error is available.
  const firstLine = message
    .split(/\r?\n/u)
    .map((line) => line.trim())
    .find(Boolean);
  if (!firstLine || Array.from(firstLine).length > 500) {
    return undefined;
  }

  const startsLikeError = /^(?:error\b|failed\b|failure\b|exception\b|unable\b|request failed\b|unexpected status\b|exceeded retry limit\b|(?:错误|失败|异常|服务繁忙|请求过多|连接超时|暂时不可用)(?:\s*[:：]|\s|$))/iu.test(
    firstLine,
  );
  const startsWithRetryableStatus = /^(?:http\s*)?(?:400|408|409|429|500|502|503|504)(?:\s*[:：-]\s*|\s+(?:bad\b|too many\b|internal\b|service\b|request\b|gateway\b|error\b|错误|失败|异常))/iu.test(
    firstLine,
  );
  const knownServiceFailure = /^(?:we(?:'re| are) currently experiencing high demand\b|too many requests\b|service unavailable\b|rate.?limit(?:ed| exceeded)?\b|request timed out\b|timed out\b)/iu.test(
    firstLine,
  );
  if (
    !(startsLikeError || startsWithRetryableStatus || knownServiceFailure) ||
    !isRetryableCodexError(firstLine)
  ) {
    return undefined;
  }
  return message;
}

export function retryDelayMs(
  settings: BridgeSettings,
  testDelayMs: number | undefined,
): number {
  if (testDelayMs !== undefined) {
    return Math.max(1, testDelayMs);
  }
  const jitter = settings.retryJitterSeconds > 0
    ? Math.floor(Math.random() * (settings.retryJitterSeconds + 1))
    : 0;
  return (settings.retryIntervalSeconds + jitter) * 1_000;
}
