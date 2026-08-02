import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const moduleDirectory = path.dirname(fileURLToPath(import.meta.url));
const packagePath = path.resolve(moduleDirectory, "..", "package.json");

export const bridgeVersion = readPackageVersion();

function readPackageVersion(): string {
  try {
    const value = JSON.parse(readFileSync(packagePath, "utf8")) as { version?: unknown };
    return typeof value.version === "string" && value.version.trim()
      ? value.version.trim()
      : "unknown";
  } catch {
    return "unknown";
  }
}
