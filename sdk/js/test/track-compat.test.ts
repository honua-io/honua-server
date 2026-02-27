import { describe, expect, it } from "vitest";

import { CompatEventBus, TrackCompat } from "../src/index.js";

describe("TrackCompat", () => {
  it("supports when() and watch() lifecycle/tracking state", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });
    const track = new TrackCompat({
      eventBus,
      trackProvider: async () => ({
        coords: {
          latitude: 0,
          longitude: 0,
        },
      }),
    });

    const loadStatusValues: unknown[] = [];
    const trackingValues: unknown[] = [];
    const loadStatusHandle = track.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const trackingHandle = track.watch("tracking", (value) => {
      trackingValues.push(value);
    });

    let callbackWidget: TrackCompat | undefined;
    const widget = await track.when((resolvedWidget) => {
      callbackWidget = resolvedWidget;
    });
    await track.start();
    track.stop();

    loadStatusHandle.remove();
    trackingHandle.remove();
    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      tracking: trackingValues.length,
    };
    await track.start();

    expect(widget).toBe(track);
    expect(callbackWidget).toBe(track);
    expect(track.loaded).toBe(true);
    expect(track.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(trackingValues).toEqual([true, false]);
    expect(seenTypes).toContain("track.loading");
    expect(seenTypes).toContain("track.loaded");
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(trackingValues).toHaveLength(watchSnapshot.tracking);
  });

  it("tracks a location and updates view center/rotation", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const view = {
      center: [0, 0] as [number, number],
      rotation: 0,
      async goTo(target: { center?: [number, number] }): Promise<void> {
        if (target.center) {
          this.center = target.center;
        }
      },
    };
    const track = new TrackCompat({
      view,
      eventBus,
      rotationEnabled: true,
      useHeadingEnabled: true,
      trackProvider: async () => ({
        coords: {
          latitude: 21,
          longitude: -157,
          heading: 45,
        },
      }),
    });

    const position = await track.start();
    expect(position.coords.latitude).toBe(21);
    expect(view.center).toEqual([-157, 21]);
    expect(view.rotation).toBe(45);
    expect(track.tracking).toBe(true);

    track.stop();
    expect(track.tracking).toBe(false);
    expect(seenTypes).toContain("track.start");
    expect(seenTypes).toContain("track.position");
    expect(seenTypes).toContain("track.stop");
  });

  it("can toggle tracking state", async () => {
    const track = new TrackCompat({
      trackProvider: async () => ({
        coords: {
          latitude: 0,
          longitude: 0,
        },
      }),
    });

    expect(await track.toggle()).toBe(true);
    expect(await track.toggle()).toBe(false);
  });
});
