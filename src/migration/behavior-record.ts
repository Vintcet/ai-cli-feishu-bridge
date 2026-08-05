import type { RuntimeName } from "../domain.js";

export const behaviorRecordVersion = 1 as const;

export const behaviorStages = [
  "ingress.hook",
  "ingress.opencode",
  "ingress.feishu",
  "core.state_committed",
  "core.decision",
  "egress.runtime_command",
  "egress.feishu",
] as const;

export type BehaviorStage = (typeof behaviorStages)[number];
export type BehaviorOutcome = "observed" | "succeeded" | "failed";

export interface BehaviorProjection {
  stage: BehaviorStage;
  kind: string;
  runtime?: RuntimeName;
  outcome: BehaviorOutcome;
  observed: unknown;
}

export interface BehaviorRecord {
  recordVersion: typeof behaviorRecordVersion;
  recordId: string;
  recordedAt: string;
  source: "node";
  stage: BehaviorStage;
  kind: string;
  traceId: string;
  runtime?: RuntimeName;
  sessionRef?: string;
  outcome: BehaviorOutcome;
  observed: unknown;
  expectedProjection: BehaviorProjection;
}

export function buildBehaviorProjection(
  input: Pick<
    BehaviorRecord,
    "stage" | "kind" | "runtime" | "outcome" | "observed"
  >,
): BehaviorProjection {
  return {
    stage: input.stage,
    kind: input.kind,
    ...(input.runtime ? { runtime: input.runtime } : {}),
    outcome: input.outcome,
    observed: canonicalizeBehaviorValue(input.observed),
  };
}

export function canonicalizeBehaviorValue(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map(canonicalizeBehaviorValue);
  }
  if (!value || typeof value !== "object") {
    return value;
  }
  return Object.fromEntries(
    Object.entries(value as Record<string, unknown>)
      .sort(([left], [right]) => left.localeCompare(right, "en"))
      .map(([key, item]) => [key, canonicalizeBehaviorValue(item)]),
  );
}

export function isBehaviorRecord(value: unknown): value is BehaviorRecord {
  if (!isRecord(value)) return false;
  if (
    value.recordVersion !== behaviorRecordVersion ||
    value.source !== "node" ||
    !behaviorStages.includes(value.stage as BehaviorStage) ||
    !nonEmpty(value.recordId) ||
    !nonEmpty(value.recordedAt) ||
    !nonEmpty(value.kind) ||
    !nonEmpty(value.traceId) ||
    !["observed", "succeeded", "failed"].includes(String(value.outcome)) ||
    !Object.prototype.hasOwnProperty.call(value, "observed") ||
    !isRecord(value.expectedProjection)
  ) {
    return false;
  }
  const expected = buildBehaviorProjection(value as unknown as BehaviorRecord);
  return JSON.stringify(expected) === JSON.stringify(value.expectedProjection);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function nonEmpty(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0;
}
