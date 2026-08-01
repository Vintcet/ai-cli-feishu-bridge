import assert from "node:assert/strict";
import test from "node:test";

import {
  findCodexAncestor,
  isProcessAlive,
  matchTrackedCodexProcessIds,
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

test("finds the real Claude Code process through hook ancestors", () => {
  const snapshots: ProcessSnapshot[] = [
    { processId: 10, parentProcessId: 20, name: "node" },
    { processId: 20, parentProcessId: 30, name: "cmd" },
    { processId: 30, parentProcessId: 0, name: "claude", startedAt: "claude-start" },
  ];

  assert.deepEqual(findCodexAncestor(10, snapshots), {
    processId: 30,
    startedAt: "claude-start",
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

test("rejects exited, reused, and non-Codex tracked processes", () => {
  const startedAt = "2026-07-31T06:26:47.8060795Z";
  const matches = matchTrackedCodexProcessIds(
    [
      { processId: 10, startedAt },
      { processId: 20, startedAt },
      { processId: 30, startedAt },
      { processId: 40 },
      { processId: 50, startedAt },
    ],
    [
      { processId: 10, parentProcessId: 0, name: "codex", startedAt },
      {
        processId: 20,
        parentProcessId: 0,
        name: "codex",
        startedAt: "2026-07-31T07:26:47.8060795Z",
      },
      { processId: 30, parentProcessId: 0, name: "pwsh", startedAt },
      { processId: 40, parentProcessId: 0, name: "codex.exe" },
      { processId: 50, parentProcessId: 0, name: "claude.exe", startedAt },
    ],
  );

  assert.deepEqual([...matches], [10, 40, 50]);
});
