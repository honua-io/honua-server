import { describe, expect, it } from "vitest";

import { CompatEventBus, ExpandCompat } from "../src/index.js";

describe("ExpandCompat", () => {
  it("supports when() and watch() lifecycle state", async () => {
    const eventBus = new CompatEventBus();
    const events: string[] = [];
    eventBus.onAny((event) => {
      events.push(event.type);
    });

    const expand = new ExpandCompat({ eventBus });
    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const loadStatusHandle = expand.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = expand.watch("loaded", (value) => {
      loadedValues.push(value);
    });

    let callbackWidget: ExpandCompat | undefined;
    const widget = await expand.when((resolvedWidget) => {
      callbackWidget = resolvedWidget;
    });

    loadStatusHandle.remove();
    loadedHandle.remove();
    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
    };
    await expand.load();

    expect(widget).toBe(expand);
    expect(callbackWidget).toBe(expand);
    expect(expand.loaded).toBe(true);
    expect(expand.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(events).toContain("expand.loading");
    expect(events).toContain("expand.loaded");
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
  });

  it("toggles expanded state and emits events", () => {
    const eventBus = new CompatEventBus();
    const events: string[] = [];
    eventBus.onAny((event) => {
      events.push(event.type);
    });

    const expand = new ExpandCompat({
      eventBus,
      expanded: false,
      content: { id: "panel" },
    });

    expect(expand.expanded).toBe(false);
    expand.expand();
    expect(expand.expanded).toBe(true);
    expand.collapse();
    expect(expand.expanded).toBe(false);
    expect(expand.toggle()).toBe(true);
    expect(expand.toggle(false)).toBe(false);
    expect(events).toEqual([
      "expand.changed",
      "expand.changed",
      "expand.changed",
      "expand.changed",
    ]);

    expand.destroy();
  });
});
