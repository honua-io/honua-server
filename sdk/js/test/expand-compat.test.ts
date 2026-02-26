import { describe, expect, it } from "vitest";

import { CompatEventBus, ExpandCompat } from "../src/index.js";

describe("ExpandCompat", () => {
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
  });
});
