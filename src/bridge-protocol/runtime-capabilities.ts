export const runtimeCapabilityNames = [
  "prompt.send",
  "prompt.queue",
  "approval.resolve",
  "input.resolve",
  "session.launch",
  "session.resume",
  "session.stop",
  "activity.stream",
] as const;

export type RuntimeCapability = (typeof runtimeCapabilityNames)[number];

export function hasRuntimeCapability(
  capabilities: ReadonlySet<RuntimeCapability>,
  capability: RuntimeCapability,
): boolean {
  return capabilities.has(capability);
}
