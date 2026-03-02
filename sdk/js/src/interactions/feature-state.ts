/**
 * Feature-state interaction helpers.
 *
 * These utilities wrap MapLibre GL JS's `setFeatureState` / `removeFeatureState`
 * behind duck-typed interfaces so the SDK carries no hard runtime dependency on
 * `maplibre-gl`.  Any object that implements {@link FeatureStateMap} works.
 *
 * @module
 */

// ── Duck-typed map interfaces ─────────────────────────────────

/** Minimal subset of a MapLibre `Map` needed for feature-state operations. */
export interface FeatureStateMap {
  setFeatureState(
    target: { source: string; id: string | number; sourceLayer?: string },
    state: Record<string, unknown>,
  ): void;
  getFeatureState(target: { source: string; id: string | number; sourceLayer?: string }): Record<string, unknown>;
  removeFeatureState(target: { source: string; id: string | number; sourceLayer?: string }, key?: string): void;
}

/** Minimal subset of a MapLibre `Map` needed for event subscription. */
export interface MapEventTarget {
  on(
    event: string,
    layerOrHandler: string | ((...args: unknown[]) => void),
    handler?: (...args: unknown[]) => void,
  ): void;
  off(
    event: string,
    layerOrHandler: string | ((...args: unknown[]) => void),
    handler?: (...args: unknown[]) => void,
  ): void;
}

/** A map that supports both feature state and events. */
export type InteractiveMap = FeatureStateMap & MapEventTarget;

// ── Feature state target ──────────────────────────────────────

/** Identifies a feature within a source for state operations. */
export interface FeatureTarget {
  source: string;
  id: string | number;
  sourceLayer?: string;
}

// ── Core helpers ──────────────────────────────────────────────

/**
 * Set state on a feature.
 *
 * Thin wrapper that constructs the MapLibre target object. Prefer this over
 * calling `map.setFeatureState()` directly when working with Honua source IDs
 * and feature IDs from query results.
 */
export function setFeatureState(
  map: FeatureStateMap,
  source: string,
  id: string | number,
  state: Record<string, unknown>,
  sourceLayer?: string,
): void {
  map.setFeatureState({ source, id, sourceLayer }, state);
}

/** Get current state for a feature. */
export function getFeatureState(
  map: FeatureStateMap,
  source: string,
  id: string | number,
  sourceLayer?: string,
): Record<string, unknown> {
  return map.getFeatureState({ source, id, sourceLayer });
}

/** Remove a single state key (or all state) from a feature. */
export function removeFeatureState(
  map: FeatureStateMap,
  source: string,
  id: string | number,
  key?: string,
  sourceLayer?: string,
): void {
  map.removeFeatureState({ source, id, sourceLayer }, key);
}

// ── Hover handler ─────────────────────────────────────────────

/** Options for {@link createHoverHandler}. */
export interface HoverHandlerOptions {
  /** Source ID to set feature state on. */
  source: string;
  /** Layer ID to listen for mouse events on. */
  layer: string;
  /** Feature-state key to toggle. @default "hover" */
  stateKey?: string;
  /** Source layer (required for vector-tile sources). */
  sourceLayer?: string;
}

/** Handle returned by {@link createHoverHandler} for cleanup. */
export interface HoverHandle {
  /** Remove all event listeners. Call this on component teardown. */
  remove(): void;
  /** The currently hovered feature ID, or `undefined`. */
  readonly hoveredId: string | number | undefined;
}

/**
 * Create a hover handler that manages `feature-state` hover state.
 *
 * Wires up `mousemove` / `mouseleave` events on the specified layer and
 * toggles a boolean state key (default `"hover"`) on the underlying source.
 * Pair with a paint expression like:
 *
 * ```ts
 * import { expr } from "@honua/sdk";
 * const opacity = expr.case([expr.featureState("hover"), 1.0], 0.5);
 * ```
 *
 * @example
 * ```ts
 * const hover = createHoverHandler(map, {
 *   source: "parcels",
 *   layer: "parcel-fill",
 * });
 * // Later:
 * hover.remove();
 * ```
 */
export function createHoverHandler(map: InteractiveMap, options: HoverHandlerOptions): HoverHandle {
  const { source, layer, stateKey = "hover", sourceLayer } = options;
  let hoveredId: string | number | undefined;

  function onMouseMove(...args: unknown[]): void {
    const e = args[0] as { features?: Array<{ id?: string | number }> };
    const featureId = e?.features?.[0]?.id;
    if (featureId === undefined || featureId === null) return;

    if (hoveredId !== undefined && hoveredId !== featureId) {
      map.setFeatureState({ source, id: hoveredId, sourceLayer }, { [stateKey]: false });
    }
    hoveredId = featureId;
    map.setFeatureState({ source, id: hoveredId, sourceLayer }, { [stateKey]: true });
  }

  function onMouseLeave(): void {
    if (hoveredId !== undefined) {
      map.setFeatureState({ source, id: hoveredId, sourceLayer }, { [stateKey]: false });
      hoveredId = undefined;
    }
  }

  map.on("mousemove", layer, onMouseMove);
  map.on("mouseleave", layer, onMouseLeave);

  return {
    remove() {
      onMouseLeave(); // clear any lingering state
      map.off("mousemove", layer, onMouseMove);
      map.off("mouseleave", layer, onMouseLeave);
    },
    get hoveredId() {
      return hoveredId;
    },
  };
}

// ── Selection handler ─────────────────────────────────────────

/** Options for {@link createSelectionHandler}. */
export interface SelectionHandlerOptions {
  /** Source ID to set feature state on. */
  source: string;
  /** Layer ID to listen for click events on. */
  layer: string;
  /** Feature-state key to toggle. @default "selected" */
  stateKey?: string;
  /** Allow multiple features to be selected simultaneously. @default false */
  multiSelect?: boolean;
  /** Source layer (required for vector-tile sources). */
  sourceLayer?: string;
  /** Called whenever the selection changes. */
  onChange?: (selectedIds: ReadonlySet<string | number>) => void;
}

/** Handle returned by {@link createSelectionHandler} for cleanup. */
export interface SelectionHandle {
  /** Remove all event listeners and clear selection state. */
  remove(): void;
  /** The set of currently selected feature IDs. */
  readonly selectedIds: ReadonlySet<string | number>;
  /** Programmatically clear the selection. */
  clearSelection(): void;
  /** Programmatically select a feature by ID. */
  select(id: string | number): void;
  /** Programmatically deselect a feature by ID. */
  deselect(id: string | number): void;
}

/**
 * Create a selection handler that manages `feature-state` selection.
 *
 * Wires up `click` events on the specified layer and toggles a boolean state
 * key (default `"selected"`) on the underlying source. Supports single-select
 * (default) and multi-select modes.
 *
 * @example
 * ```ts
 * const selection = createSelectionHandler(map, {
 *   source: "parcels",
 *   layer: "parcel-fill",
 *   onChange: (ids) => updateSidebar(ids),
 * });
 * ```
 */
export function createSelectionHandler(map: InteractiveMap, options: SelectionHandlerOptions): SelectionHandle {
  const { source, layer, stateKey = "selected", multiSelect = false, sourceLayer, onChange } = options;

  const selected = new Set<string | number>();

  function clearAll(): void {
    for (const id of selected) {
      map.setFeatureState({ source, id, sourceLayer }, { [stateKey]: false });
    }
    selected.clear();
  }

  function notifyChange(): void {
    onChange?.(selected);
  }

  function onClick(...args: unknown[]): void {
    const e = args[0] as { features?: Array<{ id?: string | number }> };
    const featureId = e?.features?.[0]?.id;
    if (featureId === undefined || featureId === null) return;

    if (selected.has(featureId)) {
      // Deselect
      map.setFeatureState({ source, id: featureId, sourceLayer }, { [stateKey]: false });
      selected.delete(featureId);
    } else {
      // New selection
      if (!multiSelect) {
        clearAll();
      }
      map.setFeatureState({ source, id: featureId, sourceLayer }, { [stateKey]: true });
      selected.add(featureId);
    }
    notifyChange();
  }

  map.on("click", layer, onClick);

  return {
    remove() {
      clearAll();
      map.off("click", layer, onClick);
    },
    get selectedIds() {
      return selected;
    },
    clearSelection() {
      clearAll();
      notifyChange();
    },
    select(id: string | number) {
      if (!multiSelect) {
        clearAll();
      }
      map.setFeatureState({ source, id, sourceLayer }, { [stateKey]: true });
      selected.add(id);
      notifyChange();
    },
    deselect(id: string | number) {
      if (selected.has(id)) {
        map.setFeatureState({ source, id, sourceLayer }, { [stateKey]: false });
        selected.delete(id);
        notifyChange();
      }
    },
  };
}
