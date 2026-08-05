import type { RuntimeAdapter } from "../bridge-protocol/runtime-adapter.js";
import type { RuntimeCapability } from "../bridge-protocol/runtime-capabilities.js";
import type { RuntimePromptMode } from "../bridge-protocol/runtime-command.js";
import type { SessionRecord } from "../domain.js";
import type { OpenCodeManager } from "../opencode-manager.js";

const capabilities: ReadonlySet<RuntimeCapability> = new Set([
  "prompt.send",
  "activity.stream",
]);

export class OpenCodeRuntimeAdapter implements RuntimeAdapter {
  readonly runtime = "opencode" as const;
  readonly capabilities = capabilities;

  constructor(
    private readonly opencode:
      | Pick<OpenCodeManager, "findActiveInstanceBySession" | "sendPrompt">
      | undefined,
  ) {}

  isReady(session: SessionRecord): boolean {
    return Boolean(
      this.opencode?.findActiveInstanceBySession(session.sessionId),
    );
  }

  async sendPrompt(
    session: SessionRecord,
    prompt: string,
    mode: RuntimePromptMode,
  ): Promise<void> {
    if (mode !== "steer") {
      throw new Error("opencode 不支持原生消息排队。");
    }
    if (!this.opencode) {
      throw new Error("opencode 支持未启用。");
    }
    await this.opencode.sendPrompt(session.sessionId, prompt);
  }
}
