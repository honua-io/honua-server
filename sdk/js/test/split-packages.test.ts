import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

describe("split package manifests", () => {
  it("keeps root package scripts for split build artifacts", () => {
    const packageJsonPath = path.join(process.cwd(), "package.json");
    const packageJson = JSON.parse(fs.readFileSync(packageJsonPath, "utf8")) as {
      scripts?: Record<string, string>;
    };

    expect(packageJson.scripts?.["build:split-packages"]).toContain("prepare-split-packages.mjs");
    expect(packageJson.scripts?.["pack:split-packages"]).toContain("dist/packages/honua-sdk");
    expect(packageJson.scripts?.["pack:split-packages"]).toContain(
      "dist/packages/honua-sdk-esri-compat",
    );
    expect(packageJson.scripts?.["pack:split-packages"]).toContain("dist/packages/honua-migrate");
  });
});
