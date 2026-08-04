import type { ApprovalStore, RouteStore } from "./domain.js";

export interface StoreRetentionOptions {
  /** 消息路由和入站去重记录的保留时长（毫秒）。 */
  routeRetentionMs?: number;
  /** 已完成审批的保留时长（毫秒）；待审批记录不受数量上限影响。 */
  approvalRetentionMs?: number;
  /** 最多保留的消息路由数量。 */
  maxMessageRoutes?: number;
  /** 最多保留的入站消息去重记录数量。 */
  maxProcessedInbound?: number;
  /** 最多保留的已完成或孤立审批数量。 */
  maxCompletedApprovals?: number;
  /** 全表保留策略检查的最短间隔（毫秒）。 */
  retentionMaintenanceIntervalMs?: number;
}

export interface StoreRetentionPolicy {
  routeRetentionMs: number;
  approvalRetentionMs: number;
  maxMessageRoutes: number;
  maxProcessedInbound: number;
  maxCompletedApprovals: number;
  maintenanceIntervalMs: number;
}

const defaults: StoreRetentionPolicy = {
  routeRetentionMs: 7 * 24 * 60 * 60 * 1000,
  approvalRetentionMs: 24 * 60 * 60 * 1000,
  maxMessageRoutes: 3_000,
  maxProcessedInbound: 5_000,
  maxCompletedApprovals: 500,
  maintenanceIntervalMs: 60_000,
};

export function resolveStoreRetentionPolicy(
  options: StoreRetentionOptions,
): StoreRetentionPolicy {
  return {
    routeRetentionMs: nonNegativeInteger(
      options.routeRetentionMs,
      defaults.routeRetentionMs,
    ),
    approvalRetentionMs: nonNegativeInteger(
      options.approvalRetentionMs,
      defaults.approvalRetentionMs,
    ),
    maxMessageRoutes: nonNegativeInteger(
      options.maxMessageRoutes,
      defaults.maxMessageRoutes,
    ),
    maxProcessedInbound: nonNegativeInteger(
      options.maxProcessedInbound,
      defaults.maxProcessedInbound,
    ),
    maxCompletedApprovals: nonNegativeInteger(
      options.maxCompletedApprovals,
      defaults.maxCompletedApprovals,
    ),
    maintenanceIntervalMs: nonNegativeInteger(
      options.retentionMaintenanceIntervalMs,
      defaults.maintenanceIntervalMs,
    ),
  };
}

export function pruneRoutes(
  routes: RouteStore,
  now: number,
  policy: StoreRetentionPolicy,
): boolean {
  let changed = false;
  const cutoff = now - policy.routeRetentionMs;
  for (const [messageId, route] of Object.entries(routes.messages)) {
    if (timestampOrOldest(route.createdAt) < cutoff) {
      delete routes.messages[messageId];
      changed = true;
    }
  }
  for (const [messageId, processedAt] of Object.entries(routes.processedInbound)) {
    if (timestampOrOldest(processedAt) < cutoff) {
      delete routes.processedInbound[messageId];
      changed = true;
    }
  }
  const messagesChanged = pruneRecordToLimit(
    routes.messages,
    policy.maxMessageRoutes,
    (route) => timestampOrOldest(route.createdAt),
  );
  const inboundChanged = pruneRecordToLimit(
    routes.processedInbound,
    policy.maxProcessedInbound,
    timestampOrOldest,
  );
  return messagesChanged || inboundChanged || changed;
}

export function pruneApprovals(
  approvals: ApprovalStore,
  now: number,
  policy: StoreRetentionPolicy,
  onDelete: (requestId: string) => void,
): boolean {
  let changed = false;
  const cutoff = now - policy.approvalRetentionMs;
  for (const [requestId, approval] of Object.entries(approvals.requests)) {
    const referenceTime = approval.status === "pending"
      ? approval.expiresAt
      : approval.resolvedAt ?? approval.createdAt;
    if (timestampOrOldest(referenceTime) < cutoff) {
      delete approvals.requests[requestId];
      onDelete(requestId);
      changed = true;
    }
  }

  const completed = Object.entries(approvals.requests).filter(
    ([, approval]) => approval.status !== "pending",
  );
  if (completed.length <= policy.maxCompletedApprovals) {
    return changed;
  }
  completed.sort(
    ([leftKey, left], [rightKey, right]) =>
      timestampOrOldest(left.resolvedAt ?? left.createdAt) -
        timestampOrOldest(right.resolvedAt ?? right.createdAt) ||
      leftKey.localeCompare(rightKey),
  );
  for (const [requestId] of completed.slice(
    0,
    completed.length - policy.maxCompletedApprovals,
  )) {
    delete approvals.requests[requestId];
    onDelete(requestId);
  }
  return true;
}

function nonNegativeInteger(value: number | undefined, fallback: number): number {
  return value !== undefined && Number.isSafeInteger(value) && value >= 0
    ? value
    : fallback;
}

function timestampOrOldest(value: string): number {
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : Number.NEGATIVE_INFINITY;
}

function pruneRecordToLimit<T>(
  record: Record<string, T>,
  maximum: number,
  timestamp: (value: T) => number,
): boolean {
  const entries = Object.entries(record);
  if (entries.length <= maximum) {
    return false;
  }
  entries.sort(
    ([leftKey, left], [rightKey, right]) =>
      timestamp(left) - timestamp(right) || leftKey.localeCompare(rightKey),
  );
  for (const [key] of entries.slice(0, entries.length - maximum)) {
    delete record[key];
  }
  return true;
}
