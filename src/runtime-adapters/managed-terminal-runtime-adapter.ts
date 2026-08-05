import type { RuntimeAdapter } from "../bridge-protocol/runtime-adapter.js";
import type { RuntimeCapability } from "../bridge-protocol/runtime-capabilities.js";
import type { RuntimePromptMode } from "../bridge-protocol/runtime-command.js";
import type { ManagedRuntimeName, SessionRecord } from "../domain.js";
import type { ManagedTerminalRouter } from "../managed-terminal.js";
import type { BehaviorRecorder } from "../migration/behavior-recorder.js";

const capabilities: ReadonlySet<RuntimeCapability> = new Set([
  "prompt.send",
  "prompt.queue",
]);

export class ManagedTerminalRuntimeAdapter implements RuntimeAdapter {
  readonly capabilities = capabilities;

  constructor(
    readonly runtime: ManagedRuntimeName,
    private readonly terminals: Pick<
      ManagedTerminalRouter,
      "isReady" | "send"
    >,
    private readonly behaviorRecorder?: BehaviorRecorder,
  ) {}

  isReady(session: SessionRecord): boolean {
    return this.terminals.isReady(session);
  }

  async sendPrompt(
    session: SessionRecord,
    prompt: string,
    mode: RuntimePromptMode,
  ): Promise<void> {
    const operation = () => this.terminals.send(session, prompt, mode);
    if (!this.behaviorRecorder) {
      await operation();
      return;
    }
    this.behaviorRecorder.record(
      "core.decision",
      "prompt.route",
      { decision: mode },
      { runtime: this.runtime, sessionId: session.sessionId },
    );
    await this.behaviorRecorder.capture(
      "egress.runtime_command",
      "prompt.send",
      { prompt, mode },
      operation,
      { runtime: this.runtime, sessionId: session.sessionId },
    );
  }
}
