import { describe, expect, it } from "vitest";

import { CompatEventBus, TimeSliderCompat } from "../src/index.js";

describe("TimeSliderCompat", () => {
  it("supports when() and watch() lifecycle and playing state", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const slider = new TimeSliderCompat({
      eventBus,
      timeExtent: {
        start: "2024-01-01T00:00:00.000Z",
        end: "2024-01-02T00:00:00.000Z",
      },
      stops: {
        interval: {
          value: 1,
          unit: "days",
        },
      },
    });

    const loadStatusValues: unknown[] = [];
    const playingValues: unknown[] = [];
    const loadStatusHandle = slider.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const playingHandle = slider.watch("playing", (value) => {
      playingValues.push(value);
    });

    let callbackWidget: TimeSliderCompat | undefined;
    const widget = await slider.when((resolvedWidget) => {
      callbackWidget = resolvedWidget;
    });
    slider.play();
    slider.stop();

    loadStatusHandle.remove();
    playingHandle.remove();
    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      playing: playingValues.length,
    };
    await slider.load();
    slider.play();

    expect(widget).toBe(slider);
    expect(callbackWidget).toBe(slider);
    expect(slider.loaded).toBe(true);
    expect(slider.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(playingValues).toEqual([true, false]);
    expect(seenTypes).toContain("timeslider.loading");
    expect(seenTypes).toContain("timeslider.loaded");
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(playingValues).toHaveLength(watchSnapshot.playing);
  });

  it("steps through configured stop values and emits update events", () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const slider = new TimeSliderCompat({
      eventBus,
      stops: {
        values: ["2024-01-01T00:00:00.000Z", "2024-02-01T00:00:00.000Z"],
      },
    });

    slider.next();
    expect(slider.timeExtent?.start.toISOString()).toBe("2024-02-01T00:00:00.000Z");

    slider.previous();
    expect(slider.timeExtent?.start.toISOString()).toBe("2024-01-01T00:00:00.000Z");
    expect(seenTypes).toContain("timeslider.updated");
    expect(seenTypes).toContain("timeslider.next");
    expect(seenTypes).toContain("timeslider.previous");
  });

  it("supports play/stop lifecycle", () => {
    const slider = new TimeSliderCompat({
      timeExtent: {
        start: "2024-01-01T00:00:00.000Z",
        end: "2024-01-02T00:00:00.000Z",
      },
      stops: {
        interval: {
          value: 1,
          unit: "days",
        },
      },
    });

    slider.play();
    expect(slider.playing).toBe(true);
    slider.next();
    expect(slider.timeExtent?.start.toISOString()).toBe("2024-01-02T00:00:00.000Z");
    slider.stop();
    expect(slider.playing).toBe(false);
  });

  it("connectLayer propagates time extent changes to the layer", () => {
    const eventBus = new CompatEventBus();
    const slider = new TimeSliderCompat({
      eventBus,
      timeExtent: {
        start: "2024-01-01T00:00:00.000Z",
        end: "2024-03-01T00:00:00.000Z",
      },
      stops: {
        interval: { value: 1, unit: "days" },
      },
    });

    const extents: Array<{ start: Date; end: Date } | undefined> = [];
    const mockLayer = {
      setTimeExtent(extent: { start: Date; end: Date } | undefined) {
        extents.push(extent);
      },
    };

    slider.connectLayer(mockLayer);

    // Initial connection should propagate current extent
    expect(extents).toHaveLength(1);
    expect(extents[0]?.start.toISOString()).toBe("2024-01-01T00:00:00.000Z");

    // Advancing slider should propagate
    slider.next();
    expect(extents).toHaveLength(2);
    expect(extents[1]?.start.toISOString()).toBe("2024-01-02T00:00:00.000Z");
  });

  it("connectLayer disconnect handle stops propagation", () => {
    const eventBus = new CompatEventBus();
    const slider = new TimeSliderCompat({
      eventBus,
      timeExtent: {
        start: "2024-01-01T00:00:00.000Z",
        end: "2024-03-01T00:00:00.000Z",
      },
      stops: {
        interval: { value: 1, unit: "days" },
      },
    });

    const extents: Array<{ start: Date; end: Date } | undefined> = [];
    const mockLayer = {
      setTimeExtent(extent: { start: Date; end: Date } | undefined) {
        extents.push(extent);
      },
    };

    const handle = slider.connectLayer(mockLayer);

    // Initial propagation
    expect(extents).toHaveLength(1);

    handle.remove();

    // After disconnect, updates should NOT propagate
    slider.next();
    expect(extents).toHaveLength(1);
  });
});
