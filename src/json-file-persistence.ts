import { randomBytes } from "node:crypto";
import { mkdir, open, readFile, rename, rm } from "node:fs/promises";
import path from "node:path";

interface JsonFilePersistenceDependencies {
  dataDirectory: string;
  persistDebounceMs: number;
  stateForFile: (filePath: string) => unknown;
  runMutation: <T>(operation: () => Promise<T>) => Promise<T>;
  awaitMutations: () => Promise<void>;
  onSafetyFlush: () => void;
}

export class JsonFilePersistence {
  private readonly dirtyFiles = new Set<string>();
  private flushTimer: ReturnType<typeof setTimeout> | undefined;
  private safetyTimer: ReturnType<typeof setInterval> | undefined;
  private flushChain: Promise<void> | undefined;
  private closePromise: Promise<void> | undefined;
  private closed = false;

  constructor(
    private readonly dependencies: JsonFilePersistenceDependencies,
  ) {}

  async read<T>(
    filePath: string,
    fallback: T,
    validate: (value: unknown) => boolean,
  ): Promise<T> {
    let text: string;
    try {
      text = await readFile(filePath, "utf8");
    } catch (error) {
      const code = (error as NodeJS.ErrnoException).code;
      if (code === "ENOENT") {
        return fallback;
      }
      throw error;
    }

    try {
      const value: unknown = JSON.parse(text);
      if (!validate(value)) {
        throw new Error("JSON structure does not match the expected store schema.");
      }
      return value as T;
    } catch (error) {
      const suffix = `${Date.now()}-${randomBytes(3).toString("hex")}`;
      const quarantinePath = `${filePath}.corrupt-${suffix}`;
      try {
        await rename(filePath, quarantinePath);
      } catch (quarantineError) {
        throw new Error(
          `Could not preserve corrupt ${path.basename(filePath)} before recovery.`,
          { cause: quarantineError },
        );
      }
      console.warn(
        `[store] Preserved invalid ${path.basename(filePath)} as ${path.basename(quarantinePath)}; using defaults.`,
      );
      await this.write(filePath, fallback);
      return fallback;
    }
  }

  async write(filePath: string, value: unknown): Promise<void> {
    const trackedState = this.dependencies.stateForFile(filePath) !== undefined;
    if (trackedState) {
      this.dirtyFiles.add(filePath);
    }
    await mkdir(this.dependencies.dataDirectory, { recursive: true });
    const temporaryPath = `${filePath}.${process.pid}.tmp`;
    try {
      const temporary = await open(temporaryPath, "w");
      try {
        await temporary.writeFile(`${JSON.stringify(value)}\n`, "utf8");
        await temporary.sync();
      } finally {
        await temporary.close();
      }
      await rename(temporaryPath, filePath);
      const committed = await open(filePath, "r+");
      try {
        await committed.sync();
      } finally {
        await committed.close();
      }
      if (process.platform !== "win32") {
        const directory = await open(path.dirname(filePath), "r");
        try {
          await directory.sync();
        } finally {
          await directory.close();
        }
      }
      if (trackedState) {
        this.dirtyFiles.delete(filePath);
      }
    } catch (error) {
      await rm(temporaryPath, { force: true }).catch(() => undefined);
      throw error;
    }
  }

  schedule(filePath: string): void {
    if (this.closed) {
      throw new Error("BridgeStore is closed.");
    }
    this.dirtyFiles.add(filePath);
    if (this.flushTimer) {
      return;
    }
    this.flushTimer = setTimeout(() => {
      this.flushTimer = undefined;
      void this.flushPending().catch((error) => {
        console.error("[store] Background flush failed:", error);
      });
    }, this.dependencies.persistDebounceMs);
    this.flushTimer.unref?.();
  }

  async flushPending(): Promise<void> {
    if (this.flushTimer) {
      clearTimeout(this.flushTimer);
      this.flushTimer = undefined;
    }
    if (this.flushChain) {
      return this.flushChain;
    }
    const run = async (): Promise<void> => {
      while (this.dirtyFiles.size > 0) {
        const files = [...this.dirtyFiles];
        await this.dependencies.runMutation(async () => {
          for (const filePath of files) {
            if (!this.dirtyFiles.has(filePath)) {
              continue;
            }
            await this.write(
              filePath,
              this.dependencies.stateForFile(filePath),
            );
          }
        });
      }
    };
    this.flushChain = run().finally(() => {
      this.flushChain = undefined;
    });
    return this.flushChain;
  }

  async close(): Promise<void> {
    if (this.closePromise) {
      return this.closePromise;
    }
    const attempt = (async () => {
      if (this.safetyTimer) {
        clearInterval(this.safetyTimer);
        this.safetyTimer = undefined;
      }
      if (this.flushTimer) {
        clearTimeout(this.flushTimer);
        this.flushTimer = undefined;
      }
      await this.dependencies.awaitMutations();
      await this.flushPending();
      this.closed = true;
    })();
    this.closePromise = attempt.catch((error) => {
      this.closePromise = undefined;
      throw error;
    });
    return this.closePromise;
  }

  startSafetyFlush(): void {
    if (this.safetyTimer || this.closed) {
      return;
    }
    this.safetyTimer = setInterval(() => {
      this.dependencies.onSafetyFlush();
      void this.flushPending().catch((error) => {
        console.error("[store] Safety flush failed:", error);
      });
    }, 5_000);
    this.safetyTimer.unref?.();
  }
}
