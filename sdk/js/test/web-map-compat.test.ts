import { describe, expect, it } from "vitest";

import { WebMapCompat } from "../src/index.js";

describe("WebMapCompat", () => {
  it("supports portalItem and when/load lifecycle", async () => {
    const map = new WebMapCompat({
      portalItem: { id: "abc123" },
    });

    expect(map.loaded).toBe(false);
    expect(map.portalItem).toEqual({ id: "abc123" });

    let callbackMap: WebMapCompat | undefined;
    const resolved = await map.when((readyMap) => {
      callbackMap = readyMap;
    });

    expect(resolved).toBe(map);
    expect(callbackMap).toBe(map);
    expect(map.loaded).toBe(true);
  });
});
