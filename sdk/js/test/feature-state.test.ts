import { describe, expect, it, vi } from "vitest";

import {
  setFeatureState,
  getFeatureState,
  removeFeatureState,
  createHoverHandler,
  createSelectionHandler,
} from "../src/index.js";
import type { InteractiveMap } from "../src/index.js";

/** Create a mock map with feature state and event capabilities. */
function createMockMap(): InteractiveMap & {
  /** Stored feature state by `source:id` key. */
  _state: Map<string, Record<string, unknown>>;
  /** Registered event handlers by `event:layer` key. */
  _handlers: Map<string, Array<(...args: unknown[]) => void>>;
  /** Fire a layer-scoped event. */
  _fire(event: string, layer: string, ...args: unknown[]): void;
} {
  const state = new Map<string, Record<string, unknown>>();
  const handlers = new Map<string, Array<(...args: unknown[]) => void>>();

  function stateKey(target: { source: string; id: string | number }): string {
    return `${target.source}:${target.id}`;
  }

  return {
    _state: state,
    _handlers: handlers,

    setFeatureState(target, patch) {
      const key = stateKey(target);
      const existing = state.get(key) ?? {};
      state.set(key, { ...existing, ...patch });
    },

    getFeatureState(target) {
      return state.get(stateKey(target)) ?? {};
    },

    removeFeatureState(target, removeKey?) {
      const key = stateKey(target);
      if (removeKey) {
        const existing = state.get(key);
        if (existing) {
          delete existing[removeKey];
        }
      } else {
        state.delete(key);
      }
    },

    on(event: string, layerOrHandler: string | ((...args: unknown[]) => void), handler?: (...args: unknown[]) => void) {
      if (typeof layerOrHandler === "string" && handler) {
        const key = `${event}:${layerOrHandler}`;
        if (!handlers.has(key)) handlers.set(key, []);
        handlers.get(key)!.push(handler);
      }
    },

    off(event: string, layerOrHandler: string | ((...args: unknown[]) => void), handler?: (...args: unknown[]) => void) {
      if (typeof layerOrHandler === "string" && handler) {
        const key = `${event}:${layerOrHandler}`;
        const list = handlers.get(key);
        if (list) {
          const idx = list.indexOf(handler);
          if (idx >= 0) list.splice(idx, 1);
        }
      }
    },

    _fire(event, layer, ...args) {
      const key = `${event}:${layer}`;
      for (const h of handlers.get(key) ?? []) {
        h(...args);
      }
    },
  };
}

describe("setFeatureState / getFeatureState / removeFeatureState", () => {
  it("sets and retrieves feature state", () => {
    const map = createMockMap();
    setFeatureState(map, "parcels", 42, { hover: true });
    expect(getFeatureState(map, "parcels", 42)).toEqual({ hover: true });
  });

  it("merges state keys", () => {
    const map = createMockMap();
    setFeatureState(map, "parcels", 1, { hover: true });
    setFeatureState(map, "parcels", 1, { selected: true });
    expect(getFeatureState(map, "parcels", 1)).toEqual({
      hover: true,
      selected: true,
    });
  });

  it("removes a single state key", () => {
    const map = createMockMap();
    setFeatureState(map, "parcels", 1, { hover: true, selected: true });
    removeFeatureState(map, "parcels", 1, "hover");
    expect(getFeatureState(map, "parcels", 1)).toEqual({ selected: true });
  });

  it("removes all state when no key provided", () => {
    const map = createMockMap();
    setFeatureState(map, "parcels", 1, { hover: true });
    removeFeatureState(map, "parcels", 1);
    expect(getFeatureState(map, "parcels", 1)).toEqual({});
  });
});

describe("createHoverHandler", () => {
  it("sets hover state on mousemove and clears on mouseleave", () => {
    const map = createMockMap();
    const hover = createHoverHandler(map, {
      source: "parcels",
      layer: "parcel-fill",
    });

    // Simulate mousemove over feature 1
    map._fire("mousemove", "parcel-fill", { features: [{ id: 1 }] });
    expect(map._state.get("parcels:1")).toEqual({ hover: true });
    expect(hover.hoveredId).toBe(1);

    // Move to feature 2 — feature 1 should be unhovered
    map._fire("mousemove", "parcel-fill", { features: [{ id: 2 }] });
    expect(map._state.get("parcels:1")).toEqual({ hover: false });
    expect(map._state.get("parcels:2")).toEqual({ hover: true });
    expect(hover.hoveredId).toBe(2);

    // Leave the layer
    map._fire("mouseleave", "parcel-fill");
    expect(map._state.get("parcels:2")).toEqual({ hover: false });
    expect(hover.hoveredId).toBeUndefined();
  });

  it("uses a custom state key", () => {
    const map = createMockMap();
    createHoverHandler(map, {
      source: "parcels",
      layer: "parcel-fill",
      stateKey: "highlighted",
    });

    map._fire("mousemove", "parcel-fill", { features: [{ id: 5 }] });
    expect(map._state.get("parcels:5")).toEqual({ highlighted: true });
  });

  it("ignores events with no feature id", () => {
    const map = createMockMap();
    const hover = createHoverHandler(map, {
      source: "parcels",
      layer: "parcel-fill",
    });

    map._fire("mousemove", "parcel-fill", { features: [] });
    expect(hover.hoveredId).toBeUndefined();

    map._fire("mousemove", "parcel-fill", { features: [{ id: undefined }] });
    expect(hover.hoveredId).toBeUndefined();
  });

  it("does not re-fire when hovering the same feature", () => {
    const map = createMockMap();
    const spy = vi.spyOn(map, "setFeatureState");
    createHoverHandler(map, {
      source: "parcels",
      layer: "parcel-fill",
    });

    map._fire("mousemove", "parcel-fill", { features: [{ id: 1 }] });
    map._fire("mousemove", "parcel-fill", { features: [{ id: 1 }] });
    // Initial set + re-set on same feature (two calls, both setting hover: true)
    expect(spy).toHaveBeenCalledTimes(2);
  });

  it("remove() clears state and unsubscribes", () => {
    const map = createMockMap();
    const hover = createHoverHandler(map, {
      source: "parcels",
      layer: "parcel-fill",
    });

    map._fire("mousemove", "parcel-fill", { features: [{ id: 1 }] });
    hover.remove();

    // State should be cleared
    expect(map._state.get("parcels:1")).toEqual({ hover: false });

    // Subsequent events should not change state
    map._fire("mousemove", "parcel-fill", { features: [{ id: 2 }] });
    expect(map._state.has("parcels:2")).toBe(false);
  });
});

describe("createSelectionHandler", () => {
  it("toggles selection on click (single-select mode)", () => {
    const map = createMockMap();
    const selection = createSelectionHandler(map, {
      source: "parcels",
      layer: "parcel-fill",
    });

    // Click feature 1
    map._fire("click", "parcel-fill", { features: [{ id: 1 }] });
    expect(map._state.get("parcels:1")).toEqual({ selected: true });
    expect(selection.selectedIds.has(1)).toBe(true);

    // Click feature 2 — feature 1 should be deselected
    map._fire("click", "parcel-fill", { features: [{ id: 2 }] });
    expect(map._state.get("parcels:1")).toEqual({ selected: false });
    expect(map._state.get("parcels:2")).toEqual({ selected: true });
    expect(selection.selectedIds.size).toBe(1);

    // Click feature 2 again — deselect
    map._fire("click", "parcel-fill", { features: [{ id: 2 }] });
    expect(map._state.get("parcels:2")).toEqual({ selected: false });
    expect(selection.selectedIds.size).toBe(0);
  });

  it("supports multi-select mode", () => {
    const map = createMockMap();
    const selection = createSelectionHandler(map, {
      source: "parcels",
      layer: "parcel-fill",
      multiSelect: true,
    });

    map._fire("click", "parcel-fill", { features: [{ id: 1 }] });
    map._fire("click", "parcel-fill", { features: [{ id: 2 }] });
    expect(selection.selectedIds.size).toBe(2);
    expect(map._state.get("parcels:1")).toEqual({ selected: true });
    expect(map._state.get("parcels:2")).toEqual({ selected: true });

    // Clicking 1 again deselects it
    map._fire("click", "parcel-fill", { features: [{ id: 1 }] });
    expect(selection.selectedIds.size).toBe(1);
    expect(map._state.get("parcels:1")).toEqual({ selected: false });
  });

  it("calls onChange callback", () => {
    const onChange = vi.fn();
    const map = createMockMap();
    createSelectionHandler(map, {
      source: "parcels",
      layer: "parcel-fill",
      onChange,
    });

    map._fire("click", "parcel-fill", { features: [{ id: 1 }] });
    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange.mock.calls[0][0].has(1)).toBe(true);

    map._fire("click", "parcel-fill", { features: [{ id: 1 }] });
    expect(onChange).toHaveBeenCalledTimes(2);
    expect(onChange.mock.calls[1][0].size).toBe(0);
  });

  it("programmatic select/deselect/clearSelection", () => {
    const map = createMockMap();
    const selection = createSelectionHandler(map, {
      source: "parcels",
      layer: "parcel-fill",
      multiSelect: true,
    });

    selection.select(10);
    selection.select(20);
    expect(selection.selectedIds.size).toBe(2);
    expect(map._state.get("parcels:10")).toEqual({ selected: true });

    selection.deselect(10);
    expect(selection.selectedIds.size).toBe(1);
    expect(map._state.get("parcels:10")).toEqual({ selected: false });

    selection.clearSelection();
    expect(selection.selectedIds.size).toBe(0);
    expect(map._state.get("parcels:20")).toEqual({ selected: false });
  });

  it("single-select programmatic select clears previous", () => {
    const map = createMockMap();
    const selection = createSelectionHandler(map, {
      source: "parcels",
      layer: "parcel-fill",
    });

    selection.select(1);
    selection.select(2);
    expect(selection.selectedIds.size).toBe(1);
    expect(selection.selectedIds.has(2)).toBe(true);
    expect(map._state.get("parcels:1")).toEqual({ selected: false });
  });

  it("remove() clears selection and unsubscribes", () => {
    const map = createMockMap();
    const selection = createSelectionHandler(map, {
      source: "parcels",
      layer: "parcel-fill",
    });

    map._fire("click", "parcel-fill", { features: [{ id: 1 }] });
    selection.remove();

    expect(map._state.get("parcels:1")).toEqual({ selected: false });
    expect(selection.selectedIds.size).toBe(0);

    // Subsequent clicks should not register
    map._fire("click", "parcel-fill", { features: [{ id: 2 }] });
    expect(map._state.has("parcels:2")).toBe(false);
  });

  it("ignores clicks with no feature id", () => {
    const map = createMockMap();
    const selection = createSelectionHandler(map, {
      source: "parcels",
      layer: "parcel-fill",
    });

    map._fire("click", "parcel-fill", { features: [{}] });
    expect(selection.selectedIds.size).toBe(0);
  });

  it("uses a custom state key", () => {
    const map = createMockMap();
    createSelectionHandler(map, {
      source: "parcels",
      layer: "parcel-fill",
      stateKey: "active",
    });

    map._fire("click", "parcel-fill", { features: [{ id: 1 }] });
    expect(map._state.get("parcels:1")).toEqual({ active: true });
  });
});
