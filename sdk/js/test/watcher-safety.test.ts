import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

function getCompatSourceDir(): string {
  return path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../src/esri-compat");
}

describe("compat watcher safety", () => {
  it("does not directly invoke listener(value|event) in compat sources", () => {
    const sourceDir = getCompatSourceDir();
    const files = fs.readdirSync(sourceDir).filter((name) => name.endsWith(".ts"));
    const violations: string[] = [];

    for (const fileName of files) {
      if (fileName === "event-bus.ts") {
        continue;
      }

      const filePath = path.join(sourceDir, fileName);
      const source = fs.readFileSync(filePath, "utf8");
      const matches = source.match(/listener\((value|event)\);/g);
      if (!matches || matches.length === 0) {
        continue;
      }
      violations.push(`${fileName}: ${matches.join(", ")}`);
    }

    expect(violations).toEqual([]);
  });
});

