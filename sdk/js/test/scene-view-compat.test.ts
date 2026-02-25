import { describe, expect, it } from "vitest";

import { SceneViewCompat } from "../src/index.js";

describe("SceneViewCompat", () => {
  it("extends map-view lifecycle defaults with scene options", async () => {
    const scene = new SceneViewCompat({
      viewingMode: "local",
      qualityProfile: "high",
      zoom: 2,
      center: [0, 0],
    });

    expect(scene.viewingMode).toBe("local");
    expect(scene.qualityProfile).toBe("high");
    await scene.goTo({ zoom: 4, center: [10, 10] });
    expect(scene.zoom).toBe(4);
    expect(scene.center).toEqual([10, 10]);
  });
});
