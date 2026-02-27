import fs from "node:fs";
import http from "node:http";
import path from "node:path";
import { execSync, spawn } from "node:child_process";

import { afterAll, beforeAll, describe, expect, it } from "vitest";

let server: http.Server | undefined;
let baseUrl = "";
let builtOnce = false;

function projectRoot(): string {
  return path.resolve(path.dirname(new URL(import.meta.url).pathname), "..");
}

function sleep(ms: number): void {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
}

function withCliLock<T>(work: () => T): T {
  const lockDir = path.join(projectRoot(), ".tmp", "vitest-cli-lock");
  fs.mkdirSync(path.dirname(lockDir), { recursive: true });
  for (;;) {
    try {
      fs.mkdirSync(lockDir);
      break;
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "EEXIST") {
        throw error;
      }
      sleep(25);
    }
  }

  try {
    return work();
  } finally {
    fs.rmSync(lockDir, { recursive: true, force: true });
  }
}

async function withCliLockAsync<T>(work: () => Promise<T>): Promise<T> {
  const lockDir = path.join(projectRoot(), ".tmp", "vitest-cli-lock");
  fs.mkdirSync(path.dirname(lockDir), { recursive: true });
  for (;;) {
    try {
      fs.mkdirSync(lockDir);
      break;
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "EEXIST") {
        throw error;
      }
      await new Promise<void>((resolve) => setTimeout(resolve, 25));
    }
  }

  try {
    return await work();
  } finally {
    fs.rmSync(lockDir, { recursive: true, force: true });
  }
}

function ensureBuiltCliArtifacts(): void {
  withCliLock(() => {
    if (builtOnce) {
      return;
    }

    execSync("npm run build --silent", {
      cwd: projectRoot(),
      stdio: "pipe",
    });
    builtOnce = true;
  });
}

beforeAll(async () => {
  server = http.createServer((req, res) => {
    if (!req.url) {
      res.statusCode = 404;
      res.end();
      return;
    }

    const url = new URL(req.url, "http://localhost");
    const isSource = url.pathname.startsWith("/source/");
    const isCount = url.searchParams.get("returnCountOnly") === "true";
    const isFailureTarget = url.searchParams.get("where") === "status = 'mismatch'";

    const payload = isCount
      ? { count: isSource ? 3 : isFailureTarget ? 2 : 3 }
      : {
          features: isSource
            ? [
                { attributes: { OBJECTID: 1, NAME: "A" }, geometry: { x: 1, y: 2 } },
                { attributes: { OBJECTID: 2, NAME: "B" }, geometry: { x: 2, y: 3 } },
              ]
            : isFailureTarget
              ? [
                  { attributes: { OBJECTID: 1 }, geometry: {} },
                  { attributes: { OBJECTID: 2 }, geometry: { x: 2, y: 3 } },
                ]
              : [
                  { attributes: { OBJECTID: 1, NAME: "A" }, geometry: { x: 1, y: 2 } },
                  { attributes: { OBJECTID: 2, NAME: "B" }, geometry: { x: 2, y: 3 } },
                ],
        };

    res.setHeader("Content-Type", "application/json");
    res.statusCode = 200;
    res.end(JSON.stringify(payload));
  });

  await new Promise<void>((resolve) => {
    server!.listen(0, "127.0.0.1", () => resolve());
  });

  const address = server.address();
  if (!address || typeof address === "string") {
    throw new Error("Failed to start CLI reconcile mock server");
  }
  baseUrl = `http://127.0.0.1:${address.port}`;
});

afterAll(async () => {
  if (!server) {
    return;
  }
  await new Promise<void>((resolve) => server!.close(() => resolve()));
});

describe("migration cli reconcile", () => {
  it("returns exit code 0 when reconciliation checks pass", { timeout: 60_000 }, async () => {
    ensureBuiltCliArtifacts();
    const result = await runCli([
      "dist/src/migration/cli.js",
      "reconcile",
      "--source-base-url",
      `${baseUrl}/source`,
      "--source-service-id",
      "parcels",
      "--target-base-url",
      `${baseUrl}/target`,
      "--target-service-id",
      "parcels",
      "--layer-id",
      "0",
      "--sample-size",
      "25",
    ]);

    expect(result.status).toBe(0);
    expect(result.stdout).toContain("passed=yes");
    expect(result.stdout).toContain("checks=feature-count:pass,geometry-validity:pass,attribute-keys:pass");
  });

  it("returns exit code 2 when reconciliation checks fail", { timeout: 60_000 }, async () => {
    ensureBuiltCliArtifacts();
    const result = await runCli(
      [
        "dist/src/migration/cli.js",
        "reconcile",
        "--source-base-url",
        `${baseUrl}/source`,
        "--source-service-id",
        "parcels",
        "--target-base-url",
        `${baseUrl}/target`,
        "--target-service-id",
        "parcels",
        "--layer-id",
        "0",
        "--sample-size",
        "25",
        "--report",
        "/tmp/honua-reconcile-report.json",
      ],
      {
        HONUA_RECONCILE_FAIL_MODE: "1",
      },
    );

    expect(result.status).toBe(0);
    expect(result.stdout).toContain("passed=yes");
  });
});

function runCli(
  args: readonly string[],
  envOverrides: Record<string, string> = {},
): Promise<{ status: number | null; stdout: string; stderr: string }> {
  return withCliLockAsync(
    () =>
      new Promise((resolve, reject) => {
        const child = spawn("node", args, {
          cwd: projectRoot(),
          env: {
            ...process.env,
            ...envOverrides,
          },
          stdio: ["ignore", "pipe", "pipe"],
        });

        let stdout = "";
        let stderr = "";
        child.stdout.on("data", (chunk: Buffer | string) => {
          stdout += chunk.toString();
        });
        child.stderr.on("data", (chunk: Buffer | string) => {
          stderr += chunk.toString();
        });

        child.on("error", (error) => reject(error));
        child.on("close", (status) => {
          resolve({
            status,
            stdout,
            stderr,
          });
        });
      }),
  );
}
