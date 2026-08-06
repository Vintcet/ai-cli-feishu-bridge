import { randomUUID } from "node:crypto";
import {
  mkdir,
  open,
  lstat,
  readFile,
  rename,
  rm,
  stat,
} from "node:fs/promises";
import path from "node:path";

export const activeOwnerLeaseDirectoryName = "bridge-active-owner.lock";
export const activeOwnerLeaseMetadataName = "owner.json";
export const activeOwnerLeaseSchemaVersion = 1;
const maximumProcessId = 2_147_483_647;
const activeOwnerLeaseFields = [
  "schemaVersion",
  "hostKind",
  "ownershipMode",
  "processId",
  "instanceName",
  "leaseId",
  "acquiredAt",
] as const;

export interface ActiveOwnerLeaseRecord {
  schemaVersion: number;
  hostKind: "node" | "dotnet";
  ownershipMode: "active";
  processId: number;
  instanceName: string;
  leaseId: string;
  acquiredAt: string;
}

export interface ActiveOwnerLeaseOptions {
  hostKind?: ActiveOwnerLeaseRecord["hostKind"];
  instanceName?: string;
  processId?: number;
  now?: () => Date;
  processAlive?: (processId: number) => boolean;
}

type ExistingLease =
  | { state: "missing" }
  | { state: "invalid" }
  | { state: "valid"; record: ActiveOwnerLeaseRecord };

export class ActiveOwnerLease {
  readonly lockDirectory: string;
  readonly metadataPath: string;

  private readonly record: ActiveOwnerLeaseRecord;
  private readonly processAlive: (processId: number) => boolean;
  private held = false;

  constructor(
    private readonly dataDirectory: string,
    options: ActiveOwnerLeaseOptions = {},
  ) {
    const processId = options.processId ?? process.pid;
    if (
      !Number.isSafeInteger(processId) ||
      processId <= 0 ||
      processId > maximumProcessId
    ) {
      throw new RangeError(
        "Active Owner lease processId must be a positive 32-bit integer.",
      );
    }
    const instanceName = options.instanceName ?? "production";
    if (!/^[A-Za-z0-9_-]+$/u.test(instanceName)) {
      throw new Error(
        "Active Owner lease instanceName may contain only letters, numbers, hyphens, and underscores.",
      );
    }
    this.lockDirectory = path.join(dataDirectory, activeOwnerLeaseDirectoryName);
    this.metadataPath = path.join(
      this.lockDirectory,
      activeOwnerLeaseMetadataName,
    );
    this.processAlive = options.processAlive ?? isActiveOwnerProcessAlive;
    this.record = {
      schemaVersion: activeOwnerLeaseSchemaVersion,
      hostKind: options.hostKind ?? "node",
      ownershipMode: "active",
      processId,
      instanceName,
      leaseId: randomUUID(),
      acquiredAt: (options.now ?? (() => new Date()))().toISOString(),
    };
  }

  get isHeld(): boolean {
    return this.held;
  }

  async acquire(): Promise<ActiveOwnerLeaseRecord> {
    if (this.held) {
      throw new Error("Active Owner lease has already been acquired.");
    }
    await mkdir(this.dataDirectory, { recursive: true });
    const stagingDirectory = path.join(
      this.dataDirectory,
      `bridge-active-owner.pending-${this.record.leaseId}`,
    );
    try {
      await this.prepareStagingDirectory(stagingDirectory);
      for (let attempt = 0; attempt < 8; attempt += 1) {
        const lockPathKind = await this.lockPathKind();
        if (lockPathKind === "file") {
          throw new Error(
            "Production Store Active Owner lease path is not a directory; refusing to replace malformed metadata.",
          );
        }
        if (lockPathKind === "directory") {
          const existing = await this.readExistingLease();
          if (existing.state === "invalid") {
            throw new Error(
              "Production Store Active Owner lease metadata is missing or invalid; refusing to replace it automatically.",
            );
          }
          if (existing.state === "valid") {
            if (this.processAlive(existing.record.processId)) {
              throw new Error(
                `Production Store already has an Active Owner (${existing.record.hostKind}, pid=${existing.record.processId}).`,
              );
            }
            if (await this.quarantineStaleLease(existing.record)) {
              continue;
            }
          }
        }
        try {
          await rename(stagingDirectory, this.lockDirectory);
          this.held = true;
          return this.record;
        } catch (error) {
          const existing = await this.readExistingLease();
          if (existing.state === "missing") {
            throw error;
          }
          if (existing.state === "invalid") {
            throw new Error(
              "Production Store Active Owner lease metadata is missing or invalid; refusing to replace it automatically.",
            );
          }
          if (this.processAlive(existing.record.processId)) {
            throw new Error(
              `Production Store already has an Active Owner (${existing.record.hostKind}, pid=${existing.record.processId}).`,
            );
          }
          if (await this.quarantineStaleLease(existing.record)) {
            continue;
          }
        }
      }
    } finally {
      await rm(stagingDirectory, { recursive: true, force: true }).catch(
        () => undefined,
      );
    }

    throw new Error(
      "Production Store Active Owner lease changed repeatedly during acquisition.",
    );
  }

  async release(): Promise<void> {
    if (!this.held) {
      return;
    }
    const existing = await this.readExistingLease();
    if (
      existing.state !== "valid" ||
      existing.record.leaseId !== this.record.leaseId
    ) {
      throw new Error(
        "Production Store Active Owner lease identity changed; refusing to remove another owner's lease.",
      );
    }
    await rm(this.lockDirectory, { recursive: true, force: false });
    this.held = false;
  }

  private async prepareStagingDirectory(stagingDirectory: string): Promise<void> {
    await mkdir(stagingDirectory);
    const metadataPath = path.join(
      stagingDirectory,
      activeOwnerLeaseMetadataName,
    );
    const handle = await open(metadataPath, "wx");
    try {
      await handle.writeFile(`${JSON.stringify(this.record)}\n`, "utf8");
      await handle.sync();
    } finally {
      await handle.close();
    }
  }

  private async readExistingLease(): Promise<ExistingLease> {
    try {
      const parsed: unknown = JSON.parse(await readFile(this.metadataPath, "utf8"));
      const record = parseActiveOwnerLeaseRecord(parsed);
      return record ? { state: "valid", record } : { state: "invalid" };
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") {
        return { state: "invalid" };
      }
      try {
        await stat(this.lockDirectory);
        return { state: "invalid" };
      } catch (statError) {
        if ((statError as NodeJS.ErrnoException).code === "ENOENT") {
          return { state: "missing" };
        }
        return { state: "invalid" };
      }
    }
  }

  private async lockPathKind(): Promise<"missing" | "directory" | "file"> {
    try {
      return (await lstat(this.lockDirectory)).isDirectory()
        ? "directory"
        : "file";
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === "ENOENT") {
        return "missing";
      }
      throw error;
    }
  }

  private async quarantineStaleLease(
    staleRecord: ActiveOwnerLeaseRecord,
  ): Promise<boolean> {
    const staleDirectory = path.join(
      this.dataDirectory,
      `bridge-active-owner.stale-${staleRecord.leaseId}`,
    );
    try {
      await rename(this.lockDirectory, staleDirectory);
    } catch (error) {
      if (
        ["ENOENT", "EEXIST", "ENOTEMPTY"].includes(
          (error as NodeJS.ErrnoException).code ?? "",
        )
      ) {
        return false;
      }
      throw error;
    }
    return true;
  }
}

function isActiveOwnerProcessAlive(processId: number): boolean {
  try {
    process.kill(processId, 0);
    return true;
  } catch (error) {
    return (error as NodeJS.ErrnoException).code === "EPERM";
  }
}

export function parseActiveOwnerLeaseRecord(
  value: unknown,
): ActiveOwnerLeaseRecord | undefined {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return undefined;
  }
  const record = value as Record<string, unknown>;
  const fields = Object.keys(record);
  if (
    fields.length !== activeOwnerLeaseFields.length ||
    activeOwnerLeaseFields.some((field) => !fields.includes(field)) ||
    record.schemaVersion !== activeOwnerLeaseSchemaVersion ||
    (record.hostKind !== "node" && record.hostKind !== "dotnet") ||
    record.ownershipMode !== "active" ||
    typeof record.processId !== "number" ||
    !Number.isSafeInteger(record.processId) ||
    record.processId <= 0 ||
    record.processId > maximumProcessId ||
    typeof record.instanceName !== "string" ||
    !/^[A-Za-z0-9_-]+$/u.test(record.instanceName) ||
    typeof record.leaseId !== "string" ||
    !/^[A-Za-z0-9_-]+$/u.test(record.leaseId) ||
    typeof record.acquiredAt !== "string" ||
    !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/u.test(
      record.acquiredAt,
    ) ||
    !Number.isFinite(Date.parse(record.acquiredAt))
  ) {
    return undefined;
  }
  return {
    schemaVersion: record.schemaVersion,
    hostKind: record.hostKind,
    ownershipMode: record.ownershipMode,
    processId: record.processId,
    instanceName: record.instanceName,
    leaseId: record.leaseId,
    acquiredAt: record.acquiredAt,
  };
}
