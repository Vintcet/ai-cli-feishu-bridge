import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";

import {
  ActiveOwnerLease,
  activeOwnerLeaseMetadataName,
  parseActiveOwnerLeaseRecord,
} from "../src/active-owner-lease.js";

async function temporaryDirectory(): Promise<string> {
  return mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-owner-lease-"));
}

test("active owner lease rejects a second live writer and releases cleanly", async () => {
  const directory = await temporaryDirectory();
  try {
    const first = new ActiveOwnerLease(directory, {
      processId: 41001,
      now: () => new Date("2026-08-06T10:00:00.000Z"),
      processAlive: (processId) => processId === 41001,
    });
    const second = new ActiveOwnerLease(directory, {
      processId: 41002,
      processAlive: (processId) => processId === 41001,
    });

    const record = await first.acquire();
    const persisted = parseActiveOwnerLeaseRecord(
      JSON.parse(await readFile(first.metadataPath, "utf8")),
    );

    assert.equal(first.isHeld, true);
    assert.deepEqual(persisted, record);
    await assert.rejects(
      second.acquire(),
      /already has an Active Owner \(node, pid=41001\)/u,
    );
    assert.deepEqual(
      (await readdir(directory)).filter((name) => name.includes("pending")),
      [],
    );

    await first.release();
    assert.equal(first.isHeld, false);
    await second.acquire();
    await second.release();
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("active owner lease reclaims a valid record owned by a dead process", async () => {
  const directory = await temporaryDirectory();
  try {
    const stale = new ActiveOwnerLease(directory, {
      processId: 42001,
      processAlive: () => false,
    });
    const staleRecord = await stale.acquire();

    const replacement = new ActiveOwnerLease(directory, {
      hostKind: "dotnet",
      instanceName: "cutover-test",
      processId: 42002,
      processAlive: () => false,
    });
    const replacementRecord = await replacement.acquire();
    const persisted = parseActiveOwnerLeaseRecord(
      JSON.parse(await readFile(replacement.metadataPath, "utf8")),
    );

    assert.equal(replacementRecord.hostKind, "dotnet");
    assert.equal(replacementRecord.processId, 42002);
    assert.deepEqual(persisted, replacementRecord);
    assert.deepEqual(
      (await readdir(directory)).filter((name) => name.includes("stale")),
      [`bridge-active-owner.stale-${staleRecord.leaseId}`],
    );
    await replacement.release();
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("active owner lease refuses malformed metadata instead of stealing it", async () => {
  const directory = await temporaryDirectory();
  try {
    const lease = new ActiveOwnerLease(directory, {
      processId: 43001,
      processAlive: () => false,
    });
    await writeFile(
      path.join(directory, "bridge-active-owner.lock"),
      "not-a-directory",
      "utf8",
    );

    await assert.rejects(lease.acquire());
  } finally {
    await rm(directory, { recursive: true, force: true });
  }

  const metadataDirectory = await temporaryDirectory();
  try {
    const lease = new ActiveOwnerLease(metadataDirectory, {
      processId: 43002,
      processAlive: () => false,
    });
    await mkdir(lease.lockDirectory);
    await writeFile(
      path.join(lease.lockDirectory, activeOwnerLeaseMetadataName),
      "{}\n",
      "utf8",
    );

    await assert.rejects(lease.acquire(), /metadata is missing or invalid/u);
  } finally {
    await rm(metadataDirectory, { recursive: true, force: true });
  }
});

test("active owner release never removes a replacement owner's lease", async () => {
  const directory = await temporaryDirectory();
  try {
    const lease = new ActiveOwnerLease(directory, {
      processId: 44001,
      processAlive: () => true,
    });
    const original = await lease.acquire();
    const replacement = {
      ...original,
      processId: 44002,
      leaseId: "replacement-lease",
    };
    await writeFile(
      lease.metadataPath,
      `${JSON.stringify(replacement)}\n`,
      "utf8",
    );

    await assert.rejects(lease.release(), /identity changed/u);
    assert.deepEqual(
      parseActiveOwnerLeaseRecord(
        JSON.parse(await readFile(lease.metadataPath, "utf8")),
      ),
      replacement,
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("active owner lease parser rejects incompatible records", () => {
  assert.equal(parseActiveOwnerLeaseRecord({}), undefined);
  assert.equal(
    parseActiveOwnerLeaseRecord({
      schemaVersion: 1,
      hostKind: "node",
      ownershipMode: "passive",
      processId: 1,
      instanceName: "production",
      leaseId: "lease-1",
      acquiredAt: "2026-08-06T10:00:00.000Z",
    }),
    undefined,
  );
  assert.equal(
    parseActiveOwnerLeaseRecord({
      schemaVersion: 1,
      hostKind: "node",
      ownershipMode: "active",
      processId: 1,
      instanceName: "production",
      leaseId: "../outside",
      acquiredAt: "2026-08-06T10:00:00.000Z",
    }),
    undefined,
  );
  assert.equal(
    parseActiveOwnerLeaseRecord({
      schemaVersion: 1,
      hostKind: "node",
      ownershipMode: "active",
      processId: 2_147_483_648,
      instanceName: "production",
      leaseId: "lease-1",
      acquiredAt: "2026-08-06T10:00:00.000Z",
    }),
    undefined,
  );
  assert.equal(
    parseActiveOwnerLeaseRecord({
      schemaVersion: 1,
      hostKind: "node",
      ownershipMode: "active",
      processId: 1,
      instanceName: "production",
      leaseId: "lease-1",
      acquiredAt: "August 6, 2026",
    }),
    undefined,
  );
  assert.equal(
    parseActiveOwnerLeaseRecord({
      schemaVersion: 1,
      hostKind: "node",
      ownershipMode: "active",
      processId: 1,
      instanceName: "production",
      leaseId: "lease-1",
      acquiredAt: "2026-08-06T10:00:00.000Z",
      futureField: true,
    }),
    undefined,
  );
});

test("node reads the shared active owner lease example", async () => {
  const example = JSON.parse(
    await readFile(
      path.join(
        process.cwd(),
        "protocol",
        "ownership",
        "v1",
        "examples",
        "active-owner-node.json",
      ),
      "utf8",
    ),
  );

  assert.deepEqual(parseActiveOwnerLeaseRecord(example), example);
});

test("node reads the shared dotnet active owner lease example", async () => {
  const example = JSON.parse(
    await readFile(
      path.join(
        process.cwd(),
        "protocol",
        "ownership",
        "v1",
        "examples",
        "active-owner-dotnet.json",
      ),
      "utf8",
    ),
  );

  assert.deepEqual(parseActiveOwnerLeaseRecord(example), example);
});
