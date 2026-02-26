import { describe, expect, it } from "vitest";

import { CompatEventBus, MapViewCompat, PopupCompat } from "../src/index.js";

describe("PopupCompat", () => {
  it("syncs with MapView popup bridge over the shared event bus", () => {
    const eventBus = new CompatEventBus();
    const view = new MapViewCompat({ eventBus });
    const widget = new PopupCompat({ view, eventBus });

    view.openPopup({
      location: [1, 2],
      title: "Feature",
      content: "Details",
      features: [{ id: 101 }],
    });

    expect(widget.visible).toBe(true);
    expect(widget.title).toBe("Feature");
    expect(widget.content).toBe("Details");
    expect(widget.location).toEqual([1, 2]);
    expect(widget.features).toEqual([{ id: 101 }]);
    expect(widget.selectedFeature).toEqual({ id: 101 });

    widget.close();
    expect(view.popup.visible).toBe(false);
    expect(widget.visible).toBe(false);
    expect(widget.features).toEqual([]);
  });

  it("supports standalone open/close with watch listeners and event emissions", () => {
    const eventBus = new CompatEventBus();
    const events: string[] = [];
    const visibility: unknown[] = [];

    eventBus.onAny((event) => {
      events.push(event.type);
    });

    const widget = new PopupCompat({ eventBus });
    widget.watch("visible", (value) => {
      visibility.push(value);
    });

    widget.open({
      location: { x: 0, y: 1 },
      title: "Standalone",
      content: "Popup",
      features: [{ id: "a" }, { id: "b" }],
    });

    expect(widget.visible).toBe(true);
    expect(widget.selectedFeature).toEqual({ id: "a" });

    widget.close();
    expect(widget.visible).toBe(false);
    expect(widget.selectedFeature).toBeUndefined();
    expect(visibility).toEqual([true, false]);
    expect(events).toContain("popup.open");
    expect(events).toContain("popup.close");

    widget.destroy();
  });
});
