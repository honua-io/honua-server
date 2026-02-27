import { describe, expect, it } from "vitest";

import { CompatEventBus, SceneViewCompat } from "../src/index.js";

describe("SceneViewCompat", () => {
  it("extends map-view lifecycle defaults with scene options", async () => {
    const scene = new SceneViewCompat({
      viewingMode: "local",
      qualityProfile: "high",
      scale: 3000000,
      rotation: 5,
      extent: { xmin: -10, ymin: -10, xmax: 10, ymax: 10 },
      spatialReference: { wkid: 3857 },
      zoom: 2,
      center: [0, 0],
    });

    expect(scene.viewingMode).toBe("local");
    expect(scene.qualityProfile).toBe("high");
    expect(scene.scale).toBe(3000000);
    expect(scene.rotation).toBe(5);
    expect(scene.extent).toEqual({ xmin: -10, ymin: -10, xmax: 10, ymax: 10 });
    expect(scene.spatialReference).toEqual({ wkid: 3857 });
    await scene.goTo({ zoom: 4, center: [10, 10], scale: 1000000, rotation: 15 });
    expect(scene.zoom).toBe(4);
    expect(scene.center).toEqual([10, 10]);
    expect(scene.scale).toBe(1000000);
    expect(scene.rotation).toBe(15);
  });

  it("supports camera-aware goTo payloads", async () => {
    const eventBus = new CompatEventBus();
    const eventTypes: string[] = [];
    eventBus.onAny((event) => {
      eventTypes.push(event.type);
    });

    const scene = new SceneViewCompat({
      eventBus,
      center: [0, 0],
      zoom: 2,
    });

    await scene.goTo(
      {
        target: {
          center: [8, 9],
          zoom: 6,
        },
        camera: {
          position: {
            x: 8,
            y: 9,
            z: 1500,
          },
          heading: 25,
          tilt: 45,
        },
      },
      { duration: 900, animate: false },
    );

    expect(scene.center).toEqual([8, 9]);
    expect(scene.zoom).toBe(6);
    expect(scene.camera).toEqual({
      position: {
        x: 8,
        y: 9,
        z: 1500,
      },
      heading: 25,
      tilt: 45,
    });
    expect(eventTypes).toContain("scene-view.camera-changed");
    expect(eventTypes).toContain("view.go-to");
  });
});
