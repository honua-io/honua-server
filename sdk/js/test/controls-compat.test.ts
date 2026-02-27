import { describe, expect, it } from "vitest";

import {
  AttributionCompat,
  BasemapToggleCompat,
  CompassCompat,
  CompatEventBus,
  FullscreenCompat,
  HomeCompat,
  LocateCompat,
  MapCompat,
  MapViewCompat,
  ScaleBarCompat,
  ZoomCompat,
} from "../src/index.js";

describe("common controls compat", () => {
  it("supports when() and watch() lifecycle state across controls", async () => {
    const eventBus = new CompatEventBus();
    const map = new MapCompat({ basemap: "streets", eventBus });
    const view = new MapViewCompat({ map, eventBus, center: [0, 0], zoom: 4 });

    const locate = new LocateCompat({
      view,
      eventBus,
      locateProvider: async () => ({
        coords: {
          latitude: 21.3069,
          longitude: -157.8583,
        },
      }),
    });
    const controls = [
      new HomeCompat({ view, eventBus }),
      new BasemapToggleCompat({ map, nextBasemap: "satellite", eventBus }),
      new ScaleBarCompat({ view, eventBus }),
      locate,
      new CompassCompat({ view, eventBus }),
      new ZoomCompat({ view, eventBus }),
      new FullscreenCompat({ view, eventBus }),
      new AttributionCompat({ eventBus }),
    ];

    const loadStatusValuesByControl = new Map<object, unknown[]>();
    const loadedValuesByControl = new Map<object, unknown[]>();
    const handles: { remove(): void }[] = [];
    for (const control of controls) {
      const loadStatusValues: unknown[] = [];
      const loadedValues: unknown[] = [];
      loadStatusValuesByControl.set(control, loadStatusValues);
      loadedValuesByControl.set(control, loadedValues);
      handles.push(
        control.watch("loadStatus", (value: unknown) => {
          loadStatusValues.push(value);
        }),
      );
      handles.push(
        control.watch("loaded", (value: unknown) => {
          loadedValues.push(value);
        }),
      );

      const widget = await control.when();
      expect(widget).toBe(control);
      expect(control.loaded).toBe(true);
      expect(control.loadStatus).toBe("loaded");
    }

    for (const handle of handles) {
      handle.remove();
    }
    for (const control of controls) {
      const loadStatusValues = loadStatusValuesByControl.get(control) ?? [];
      const loadedValues = loadedValuesByControl.get(control) ?? [];
      expect(loadStatusValues).toEqual(["loading", "loaded"]);
      expect(loadedValues).toEqual([true]);
    }
  });

  it("emits watch updates for control property changes", async () => {
    const eventBus = new CompatEventBus();
    const map = new MapCompat({ basemap: "streets", eventBus });
    const view = new MapViewCompat({ map, eventBus, center: [0, 0], zoom: 4 });

    const locate = new LocateCompat({
      view,
      eventBus,
      locateProvider: async () => ({
        coords: {
          latitude: 21.3069,
          longitude: -157.8583,
        },
      }),
    });
    const compass = new CompassCompat({ view, eventBus });
    const zoom = new ZoomCompat({ view, eventBus });
    const fullscreen = new FullscreenCompat({ eventBus });
    const attribution = new AttributionCompat({ eventBus });
    const scaleBar = new ScaleBarCompat({ view, eventBus });

    const positionValues: unknown[] = [];
    const orientationValues: unknown[] = [];
    const zoomValues: unknown[] = [];
    const activeValues: unknown[] = [];
    const textValues: unknown[] = [];
    const scaleTextValues: unknown[] = [];

    const positionHandle = locate.watch("lastPosition", (value) => {
      positionValues.push(value);
    });
    const orientationHandle = compass.watch("orientation", (value) => {
      orientationValues.push(value);
    });
    const zoomHandle = zoom.watch("zoom", (value) => {
      zoomValues.push(value);
    });
    const activeHandle = fullscreen.watch("active", (value) => {
      activeValues.push(value);
    });
    const textHandle = attribution.watch("text", (value) => {
      textValues.push(value);
    });
    const scaleTextHandle = scaleBar.watch("text", (value) => {
      scaleTextValues.push(value);
    });

    await locate.locate();
    compass.rotateTo(45);
    zoom.zoomIn();
    fullscreen.enter();
    fullscreen.exit();
    attribution.addAttribution("Source A");
    scaleBar.refresh();

    positionHandle.remove();
    orientationHandle.remove();
    zoomHandle.remove();
    activeHandle.remove();
    textHandle.remove();
    scaleTextHandle.remove();

    await locate.locate();
    compass.rotateTo(60);
    zoom.zoomIn();
    fullscreen.enter();
    attribution.addAttribution("Source B");

    expect(positionValues).toHaveLength(1);
    expect(orientationValues).toEqual([45]);
    expect(zoomValues).toEqual([5]);
    expect(activeValues).toEqual([true, false]);
    expect(textValues).toEqual(["Source A"]);
    expect(scaleTextValues).not.toEqual([]);
  });

  it("HomeCompat resets view center/zoom", async () => {
    const eventBus = new CompatEventBus();
    const view = new MapViewCompat({ eventBus, center: [0, 0], zoom: 4 });
    const home = new HomeCompat({ view, eventBus });

    await view.goTo({ center: [10, 20], zoom: 8 });
    expect(view.center).toEqual([10, 20]);
    expect(view.zoom).toBe(8);

    await home.go();
    expect(view.center).toEqual([0, 0]);
    expect(view.zoom).toBe(4);
  });

  it("BasemapToggleCompat swaps current and next basemap", () => {
    const map = new MapCompat({ basemap: "streets" }) as MapCompat & { basemap: unknown };
    const toggle = new BasemapToggleCompat({
      map,
      nextBasemap: "satellite",
      eventBus: map.eventBus,
    });

    expect(toggle.activeBasemap).toBe("streets");
    expect(toggle.nextBasemap).toBe("satellite");
    expect(toggle.toggle()).toBe("satellite");
    expect(map.basemap).toBe("satellite");
    expect(toggle.nextBasemap).toBe("streets");

    map.setBasemap("gray");
    expect(toggle.activeBasemap).toBe("gray");

    toggle.destroy();
  });

  it("ScaleBarCompat refreshes text when view zoom changes", async () => {
    const eventBus = new CompatEventBus();
    const view = new MapViewCompat({ eventBus, center: [0, 0], zoom: 3 });
    const scaleBar = new ScaleBarCompat({ view, eventBus, unit: "dual" });

    const first = scaleBar.text;
    expect(first).toContain("1:");
    expect(first).toContain("/");

    await view.goTo({ zoom: 5 });
    const second = scaleBar.text;
    expect(second).toContain("1:");
    expect(second).toContain("/");
    expect(second).not.toBe(first);

    scaleBar.destroy();
  });

  it("LocateCompat goes to located coordinates and emits success", async () => {
    const eventBus = new CompatEventBus();
    const events: string[] = [];
    eventBus.onAny((event) => {
      events.push(event.type);
    });

    const view = new MapViewCompat({ eventBus, center: [0, 0], zoom: 2 });
    const locate = new LocateCompat({
      view,
      eventBus,
      zoom: 12,
      locateProvider: async () => ({
        coords: {
          latitude: 21.3069,
          longitude: -157.8583,
          accuracy: 10,
        },
      }),
    });

    const position = await locate.locate();
    expect(position.coords.latitude).toBe(21.3069);
    expect(position.coords.longitude).toBe(-157.8583);
    expect(view.center).toEqual([-157.8583, 21.3069]);
    expect(view.zoom).toBe(12);
    expect(events).toContain("locate.start");
    expect(events).toContain("locate.success");
  });

  it("CompassCompat rotates and resets view rotation", () => {
    const view = new MapViewCompat({ zoom: 2 }) as MapViewCompat & { rotation?: number };
    view.rotation = 23;

    const compass = new CompassCompat({ view });
    expect(compass.orientation).toBe(23);

    expect(compass.rotateTo(45)).toBe(45);
    expect(view.rotation).toBe(45);
    expect(compass.goToNorth()).toBe(0);
    expect(view.rotation).toBe(0);
  });

  it("ZoomCompat adjusts view zoom level", () => {
    const view = new MapViewCompat({ zoom: 4 });
    const zoom = new ZoomCompat({ view });

    expect(zoom.zoomIn()).toBe(5);
    expect(view.zoom).toBe(5);
    expect(zoom.zoomOut(2)).toBe(3);
    expect(view.zoom).toBe(3);
  });

  it("FullscreenCompat toggles active state", () => {
    const fullscreen = new FullscreenCompat();

    expect(fullscreen.active).toBe(false);
    fullscreen.enter();
    expect(fullscreen.active).toBe(true);
    expect(fullscreen.toggle()).toBe(false);
    expect(fullscreen.active).toBe(false);
  });

  it("AttributionCompat manages attribution text", () => {
    const attribution = new AttributionCompat({ itemDelimiter: " • " });

    attribution.addAttribution("Source A");
    attribution.addAttribution("Source B");
    expect(attribution.getText()).toBe("Source A • Source B");
    expect(attribution.removeAttribution("Source A")).toBe(true);
    expect(attribution.getText()).toBe("Source B");
    expect(attribution.removeAttribution("Missing")).toBe(false);
  });
});
