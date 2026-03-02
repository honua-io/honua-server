import { describe, expect, it } from "vitest";
import { access, readFile } from "node:fs/promises";

describe("distribution smoke", () => {
  it("loads built entrypoint exports", async () => {
    const mod = await import("../dist/src/index.js");
    expect(typeof mod.createServer).toBe("function");
    expect(typeof mod.resolveRuntimeOptions).toBe("function");
  });

  it("keeps node shebang in built CLI entrypoint", async () => {
    await access("dist/src/index.js");
    const text = await readFile("dist/src/index.js", "utf8");
    expect(text.startsWith("#!/usr/bin/env node")).toBe(true);
  });
});
