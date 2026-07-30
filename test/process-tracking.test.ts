import assert from "node:assert/strict";
import test from "node:test";

import {
  findCodexAncestor,
  isProcessAlive,
  type ProcessSnapshot,
} from "../src/process-tracking.js";

test("finds the real codex process through shell and hook ancestors", () => {
  const snapshots: ProcessSnapshot[] = [
    { processId: 10, parentProcessId: 20, name: "node", startedAt: "node" },
    { processId: 20, parentProcessId: 30, name: "cmd" },
    { processId: 30, parentProcessId: 40, name: "codex-command-runner-0.146.0" },
    { processId: 40, parentProcessId: 50, name: "codex", startedAt: "codex-start" },
    { processId: 50, parentProcessId: 0, name: "pwsh" },
  ];

  assert.deepEqual(findCodexAncestor(10, snapshots), {
    processId: 40,
    startedAt: "codex-start",
  });
});

test("handles missing or cyclic ancestor data safely", () => {
  assert.equal(findCodexAncestor(1, []), undefined);
  assert.equal(
    findCodexAncestor(1, [
      { processId: 1, parentProcessId: 2, name: "node" },
      { processId: 2, parentProcessId: 1, name: "cmd" },
    ]),
    undefined,
  );
});

test("checks process liveness without terminating it", () => {
  assert.equal(isProcessAlive(process.pid), true);
  assert.equal(isProcessAlive(-1), false);
});
