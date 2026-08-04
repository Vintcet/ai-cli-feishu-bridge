import { execFile } from "node:child_process";
import { createServer } from "node:net";
import path from "node:path";
import { promisify } from "node:util";

const execFileAsync = promisify(execFile);
const listeningForeignAddresses = new Set(["0.0.0.0:0", "[::]:0", "*:*"]);
const localListenHosts = new Set(["127.0.0.1", "0.0.0.0", "[::]", "[::1]"]);

export async function enumerateLocalOpenCodePorts(): Promise<number[]> {
  try {
    const systemRoot = process.env.SystemRoot ?? "C:\\Windows";
    const netstat = path.join(systemRoot, "System32", "netstat.exe");
    const { stdout } = await execFileAsync(netstat, ["-ano", "-p", "tcp"], {
      timeout: 10_000,
      windowsHide: true,
      maxBuffer: 1024 * 1024,
    });
    const ports = new Set<number>();
    for (const rawLine of stdout.split(/\r?\n/)) {
      const line = rawLine.trim();
      if (!/^TCP\b/i.test(line)) {
        continue;
      }
      const fields = line.split(/\s+/);
      const local = fields[1];
      const foreign = fields[2];
      if (!local || !foreign || !listeningForeignAddresses.has(foreign)) {
        continue;
      }
      const pid = Number(fields[fields.length - 1]);
      if (!Number.isSafeInteger(pid) || pid <= 0) {
        continue;
      }
      const lastColon = local.lastIndexOf(":");
      const host = lastColon > 0 ? local.slice(0, lastColon) : "";
      const port = Number(lastColon >= 0 ? local.slice(lastColon + 1) : "");
      if (!localListenHosts.has(host)) {
        continue;
      }
      if (Number.isSafeInteger(port) && port > 0 && port <= 65535) {
        ports.add(port);
      }
    }
    return [...ports];
  } catch {
    return [];
  }
}

export async function isLocalOpenCodePortAvailable(
  port: number,
): Promise<boolean> {
  return await new Promise<boolean>((resolve) => {
    const server = createServer();
    let settled = false;
    const finish = (available: boolean): void => {
      if (settled) return;
      settled = true;
      resolve(available);
    };
    server.unref();
    server.once("error", () => finish(false));
    server.listen({ host: "127.0.0.1", port, exclusive: true }, () => {
      server.close((error) => finish(!error));
    });
  });
}
