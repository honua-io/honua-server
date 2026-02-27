import { describe, expect, it } from "vitest";

import { CompatEventBus, SceneViewCompat } from "../src/index.js";

describe("SceneViewCompat", () => {
  it("extends map-view lifecycle defaults with scene options", async () => {
    const eventBus = new CompatEventBus();
    const eventTypes: string[] = [];
    eventBus.onAny((event) => {
      eventTypes.push(event.type);
    });

    const scene = new SceneViewCompat({
      eventBus,
      viewingMode: "local",
      qualityProfile: "high",
      camera: { heading: 10, tilt: 5 },
      scale: 3000000,
      rotation: 5,
      extent: { xmin: -10, ymin: -10, xmax: 10, ymax: 10 },
      spatialReference: { wkid: 3857 },
      zoom: 2,
      center: [0, 0],
    });

    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const viewingModeValues: unknown[] = [];
    const qualityProfileValues: unknown[] = [];
    const cameraValues: unknown[] = [];
    const loadStatusHandle = scene.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = scene.watch("loaded", (value) => {
      loadedValues.push(value);
    });
    const viewingModeHandle = scene.watch("viewingMode", (value) => {
      viewingModeValues.push(value);
    });
    const qualityProfileHandle = scene.watch("qualityProfile", (value) => {
      qualityProfileValues.push(value);
    });
    const cameraHandle = scene.watch("camera", (value) => {
      cameraValues.push(value);
    });

    let callbackScene: SceneViewCompat | undefined;
    const resolved = await scene.when((readyScene) => {
      callbackScene = readyScene;
    });

    expect(scene.viewingMode).toBe("local");
    expect(scene.qualityProfile).toBe("high");
    expect(scene.scale).toBe(3000000);
    expect(scene.rotation).toBe(5);
    expect(scene.extent).toEqual({ xmin: -10, ymin: -10, xmax: 10, ymax: 10 });
    expect(scene.spatialReference).toEqual({ wkid: 3857 });

    scene.setViewingMode("global");
    scene.setQualityProfile("medium");
    scene.setCamera({ heading: 25, tilt: 35 });

    loadStatusHandle.remove();
    loadedHandle.remove();
    viewingModeHandle.remove();
    qualityProfileHandle.remove();
    cameraHandle.remove();
    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
      viewingMode: viewingModeValues.length,
      qualityProfile: qualityProfileValues.length,
      camera: cameraValues.length,
    };

    await scene.load();
    scene.setViewingMode("local");
    scene.setQualityProfile("high");
    scene.setCamera({ heading: 1, tilt: 1 });

    expect(resolved).toBe(scene);
    expect(callbackScene).toBe(scene);
    expect(scene.loaded).toBe(true);
    expect(scene.loadStatus).toBe("loaded");
    await scene.goTo({ zoom: 4, center: [10, 10], scale: 1000000, rotation: 15 });
    expect(scene.zoom).toBe(4);
    expect(scene.center).toEqual([10, 10]);
    expect(scene.scale).toBe(1000000);
    expect(scene.rotation).toBe(15);
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(viewingModeValues).toEqual(["global"]);
    expect(qualityProfileValues).toEqual(["medium"]);
    expect(cameraValues).toEqual([{ heading: 25, tilt: 35 }]);
    expect(eventTypes).toContain("scene-view.viewing-mode-changed");
    expect(eventTypes).toContain("scene-view.quality-profile-changed");
    expect(eventTypes).toContain("scene-view.camera-changed");
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
    expect(viewingModeValues).toHaveLength(watchSnapshot.viewingMode);
    expect(qualityProfileValues).toHaveLength(watchSnapshot.qualityProfile);
    expect(cameraValues).toHaveLength(watchSnapshot.camera);
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
