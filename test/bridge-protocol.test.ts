import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

import { isRuntimeCommand } from "../src/bridge-protocol/runtime-command.js";
import { isRuntimeEvent } from "../src/bridge-protocol/runtime-event.js";

function sharedExample(name: string): unknown {
  return JSON.parse(
    readFileSync(new URL(`../protocol/v1/examples/${name}`, import.meta.url), "utf8"),
  );
}

test("accepts versioned runtime events and rejects incomplete envelopes", () => {
  const event = sharedExample("approval-requested.json");
  assert.equal(isRuntimeEvent(event), true);
  assert.equal(typeof event, "object");
  assert.notEqual(event, null);
  if (typeof event !== "object" || event === null) return;
  assert.equal(isRuntimeEvent({ ...event, protocolVersion: 2 }), false);
  assert.equal(isRuntimeEvent({ ...event, traceId: "" }), false);
  assert.equal(isRuntimeEvent({ ...event, payload: { title: "缺少 ID" } }), false);
});

test("validates runtime command type and payload fields", () => {
  const command = sharedExample("prompt-send.json");
  assert.equal(isRuntimeCommand(command), true);
  assert.equal(typeof command, "object");
  assert.notEqual(command, null);
  if (typeof command !== "object" || command === null) return;
  assert.equal(
    isRuntimeCommand({ ...command, commandType: "unknown.command" }),
    false,
  );
  assert.equal(
    isRuntimeCommand({ ...command, payload: { prompt: "继续", mode: "later" } }),
    false,
  );
});

test("JSON Schemas expose the same event and command catalogs", () => {
  const commandSchema = sharedExample("../runtime-command.schema.json") as {
    allOf: Array<{ properties?: { commandType?: { enum?: string[] } } }>;
  };
  const eventSchema = sharedExample("../runtime-event.schema.json") as {
    allOf: Array<{ properties?: { eventType?: { enum?: string[] } } }>;
  };

  assert.deepEqual(commandSchema.allOf[1]?.properties?.commandType?.enum, [
    "prompt.send",
    "approval.resolve",
    "input.resolve",
    "session.launch",
    "session.resume",
    "session.stop",
  ]);
  assert.deepEqual(eventSchema.allOf[1]?.properties?.eventType?.enum, [
    "session.started",
    "session.ended",
    "turn.started",
    "turn.activity",
    "turn.completed",
    "turn.failed",
    "approval.requested",
    "approval.resolved_externally",
    "input.requested",
    "input.resolved_externally",
    "runtime.connected",
    "runtime.disconnected",
  ]);
});
