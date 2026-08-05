import type { RuntimeName, SessionRecord } from "../domain.js";
import type { RuntimeCapability } from "./runtime-capabilities.js";
import type { RuntimePromptMode } from "./runtime-command.js";

export interface RuntimeAdapter {
  readonly runtime: RuntimeName;
  readonly capabilities: ReadonlySet<RuntimeCapability>;

  isReady(session: SessionRecord): boolean;

  sendPrompt(
    session: SessionRecord,
    prompt: string,
    mode: RuntimePromptMode,
  ): Promise<void>;
}
