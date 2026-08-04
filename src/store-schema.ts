export function isPlainRecord(
  value: unknown,
): value is Record<string, unknown> {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function isOptionalString(value: unknown): boolean {
  return value === undefined || typeof value === "string";
}

export function isBindingStoreValue(value: unknown): boolean {
  if (!isPlainRecord(value) || !isPlainRecord(value.users)) {
    return false;
  }
  if (
    !isOptionalString(value.ownerOpenId) ||
    !isOptionalString(value.pairingCode)
  ) {
    return false;
  }
  return Object.values(value.users).every(
    (binding) =>
      isPlainRecord(binding) &&
      typeof binding.openId === "string" &&
      typeof binding.chatId === "string" &&
      typeof binding.chatType === "string" &&
      typeof binding.boundAt === "string",
  );
}

export function isSessionStoreValue(value: unknown): boolean {
  if (!isPlainRecord(value) || !isPlainRecord(value.sessions)) {
    return false;
  }
  return Object.values(value.sessions).every(
    (session) =>
      isPlainRecord(session) &&
      typeof session.sessionId === "string" &&
      typeof session.cwd === "string" &&
      typeof session.status === "string" &&
      typeof session.lastSeenAt === "string",
  );
}

export function isRouteStoreValue(value: unknown): boolean {
  if (!isPlainRecord(value)) {
    return false;
  }
  if (value.messages !== undefined && !isPlainRecord(value.messages)) {
    return false;
  }
  if (
    value.processedInbound !== undefined &&
    !isPlainRecord(value.processedInbound)
  ) {
    return false;
  }
  const messages = isPlainRecord(value.messages) ? value.messages : {};
  const processedInbound = isPlainRecord(value.processedInbound)
    ? value.processedInbound
    : {};
  return (
    Object.values(messages).every(
      (route) =>
        isPlainRecord(route) &&
        typeof route.messageId === "string" &&
        typeof route.sessionId === "string" &&
        typeof route.chatId === "string" &&
        typeof route.kind === "string" &&
        typeof route.createdAt === "string",
    ) &&
    Object.values(processedInbound).every(
      (timestamp) => typeof timestamp === "string",
    )
  );
}

export function isApprovalStoreValue(value: unknown): boolean {
  if (!isPlainRecord(value) || !isPlainRecord(value.requests)) {
    return false;
  }
  return Object.values(value.requests).every(
    (approval) =>
      isPlainRecord(approval) &&
      typeof approval.requestId === "string" &&
      typeof approval.sessionId === "string" &&
      typeof approval.turnId === "string" &&
      typeof approval.cwd === "string" &&
      typeof approval.toolName === "string" &&
      typeof approval.toolPreview === "string" &&
      typeof approval.createdAt === "string" &&
      typeof approval.expiresAt === "string" &&
      typeof approval.status === "string" &&
      Array.isArray(approval.messageIds) &&
      approval.messageIds.every((messageId) => typeof messageId === "string"),
  );
}
