/**
 * HonuaMap — Source/Layer separation for multi-protocol geospatial maps.
 *
 * Manages named data sources (protocol-aware: Feature Service, Map Service,
 * OGC Features) separately from named rendering layers. One source can back
 * multiple layers; layers reference sources by name.
 *
 * Does **not** depend on `maplibre-gl` at runtime. Produces a
 * MapLibre-compatible style via {@link HonuaMap.toStyle} and exposes the
 * underlying SDK surface instances via {@link HonuaMap.getSource}.
 *
 * @module
 */

import type { HonuaClient } from "../core/client.js";
import {
  HonuaFeatureLayer,
  HonuaMapService,
  type HonuaOgcFeatureCollection,
  HonuaOgcFeatures,
} from "../core/surfaces.js";
import { parseFeatureLayerUrl, parseMapServiceUrl } from "../esri-compat/url.js";
import type {
  HonuaLayerSpecification,
  HonuaSourceSpecification,
  HonuaStyleSpecification,
} from "../style/specification.js";
import {
  isFeatureServiceSource,
  isHonuaSource,
  isMapServiceSource,
  isOgcFeaturesSource,
  parseOgcFeaturesUrl,
} from "../style/specification.js";

// ── Types ─────────────────────────────────────────────────────

/** A resolved Honua source: either an SDK surface instance or `null` for native MapLibre sources. */
export type ResolvedMapSource = HonuaFeatureLayer | HonuaMapService | HonuaOgcFeatureCollection | null;

/** Options for constructing a {@link HonuaMap}. */
export interface HonuaMapOptions {
  /** The Honua client used to instantiate protocol surfaces. */
  client: HonuaClient;
  /** Optional initial style. Sources and layers are loaded from this. */
  style?: HonuaStyleSpecification;
}

/** Snapshot of a layer with its current properties. */
export interface LayerSnapshot extends HonuaLayerSpecification {
  /** The source this layer references. */
  source: string;
}

// ── Events ────────────────────────────────────────────────────

/** Events emitted by HonuaMap. */
export type HonuaMapEvent =
  | { type: "source-added"; sourceId: string }
  | { type: "source-removed"; sourceId: string }
  | { type: "layer-added"; layerId: string }
  | { type: "layer-removed"; layerId: string }
  | { type: "layer-moved"; layerId: string; beforeId: string | undefined };

export type HonuaMapEventListener = (event: HonuaMapEvent) => void;

// ── HonuaMap ──────────────────────────────────────────────────

/**
 * A protocol-aware map that separates data sources from rendering layers.
 *
 * @example
 * ```ts
 * const map = new HonuaMap({ client });
 *
 * map.addSource("parcels", {
 *   type: "honua-feature-service",
 *   url: "https://gis.example.com/rest/services/parcels/FeatureServer/0",
 *   definitionExpression: "status = 'active'",
 * });
 *
 * map.addLayer({
 *   id: "parcel-fill",
 *   source: "parcels",
 *   type: "fill",
 *   paint: { "fill-color": "#088", "fill-opacity": 0.5 },
 * });
 *
 * map.addLayer({
 *   id: "parcel-labels",
 *   source: "parcels",
 *   type: "symbol",
 *   layout: { "text-field": ["get", "parcel_id"] },
 * });
 *
 * // Access the underlying surface for queries
 * const parcels = map.getSource("parcels"); // HonuaFeatureLayer
 * const count = await (parcels as HonuaFeatureLayer).queryFeatureCount();
 * ```
 */
export class HonuaMap {
  readonly #client: HonuaClient;
  readonly #sources = new Map<
    string,
    { spec: HonuaSourceSpecification | { type: string; [key: string]: unknown }; resolved: ResolvedMapSource }
  >();
  readonly #layers: Array<{ id: string; spec: HonuaLayerSpecification }> = [];
  readonly #listeners = new Set<HonuaMapEventListener>();

  constructor(options: HonuaMapOptions) {
    this.#client = options.client;

    if (options.style) {
      this.#loadStyle(options.style);
    }
  }

  // ── Source management ───────────────────────────────────────

  /**
   * Register a named data source.
   *
   * Honua sources (`honua-feature-service`, `honua-map-service`,
   * `honua-ogc-features`) are resolved into SDK surface instances.
   * Native MapLibre sources (`vector`, `raster`, `geojson`) are stored
   * as-is and resolve to `null`.
   *
   * @throws If a source with the same name already exists.
   */
  addSource(name: string, spec: HonuaSourceSpecification | { type: string; [key: string]: unknown }): void {
    if (this.#sources.has(name)) {
      throw new Error(`Source "${name}" already exists. Remove it first.`);
    }
    const resolved = this.#resolveSource(spec);
    this.#sources.set(name, { spec, resolved });
    this.#emit({ type: "source-added", sourceId: name });
  }

  /**
   * Remove a named data source and all layers that reference it.
   *
   * @returns The IDs of layers that were removed.
   */
  removeSource(name: string): string[] {
    if (!this.#sources.has(name)) return [];

    // Collect dependent layers in order, then remove back-to-front
    const removed: string[] = [];
    const indicesToRemove: number[] = [];
    for (let i = 0; i < this.#layers.length; i++) {
      if (this.#layers[i].spec.source === name) {
        removed.push(this.#layers[i].id);
        indicesToRemove.push(i);
      }
    }
    for (let i = indicesToRemove.length - 1; i >= 0; i--) {
      this.#layers.splice(indicesToRemove[i], 1);
    }
    for (const id of removed) {
      this.#emit({ type: "layer-removed", layerId: id });
    }

    this.#sources.delete(name);
    this.#emit({ type: "source-removed", sourceId: name });
    return removed;
  }

  /**
   * Get the resolved SDK surface for a source.
   *
   * Returns `null` for native MapLibre sources (they should be handled
   * by MapLibre directly). Returns `undefined` if the source does not exist.
   */
  getSource(name: string): ResolvedMapSource | undefined {
    return this.#sources.get(name)?.resolved;
  }

  /** Get the raw specification for a source. */
  getSourceSpec(name: string): HonuaSourceSpecification | { type: string; [key: string]: unknown } | undefined {
    return this.#sources.get(name)?.spec;
  }

  /** Check whether a source with the given name exists. */
  hasSource(name: string): boolean {
    return this.#sources.has(name);
  }

  /** Get all registered source names. */
  get sourceIds(): string[] {
    return [...this.#sources.keys()];
  }

  // ── Layer management ────────────────────────────────────────

  /**
   * Add a rendering layer.
   *
   * Layers reference a source by name. Multiple layers can reference the
   * same source (e.g. a fill layer and a symbol layer over the same
   * Feature Service).
   *
   * @param spec - Layer specification (must include `id`, `source`, `type`).
   * @param beforeId - Insert this layer before the layer with this ID.
   *   If omitted, appends to the end.
   * @throws If the referenced source does not exist.
   * @throws If a layer with the same ID already exists.
   */
  addLayer(spec: HonuaLayerSpecification, beforeId?: string): void {
    const layerSource = spec.source;
    if (layerSource && !this.#sources.has(layerSource)) {
      throw new Error(`Cannot add layer "${spec.id}": source "${layerSource}" does not exist.`);
    }
    if (this.#layers.some((l) => l.id === spec.id)) {
      throw new Error(`Layer "${spec.id}" already exists.`);
    }

    if (beforeId) {
      const idx = this.#layers.findIndex((l) => l.id === beforeId);
      if (idx === -1) {
        throw new Error(`Cannot insert before "${beforeId}": layer not found.`);
      }
      this.#layers.splice(idx, 0, { id: spec.id, spec });
    } else {
      this.#layers.push({ id: spec.id, spec });
    }

    this.#emit({ type: "layer-added", layerId: spec.id });
  }

  /** Remove a layer by ID. Returns `true` if the layer was found and removed. */
  removeLayer(id: string): boolean {
    const idx = this.#layers.findIndex((l) => l.id === id);
    if (idx === -1) return false;
    this.#layers.splice(idx, 1);
    this.#emit({ type: "layer-removed", layerId: id });
    return true;
  }

  /** Get a layer specification by ID. */
  getLayer(id: string): HonuaLayerSpecification | undefined {
    return this.#layers.find((l) => l.id === id)?.spec;
  }

  /** Check whether a layer with the given ID exists. */
  hasLayer(id: string): boolean {
    return this.#layers.some((l) => l.id === id);
  }

  /**
   * Move a layer to a new position in the render order.
   *
   * @param id - The layer to move.
   * @param beforeId - Place it before this layer. If omitted, moves to end.
   */
  moveLayer(id: string, beforeId?: string): void {
    const fromIdx = this.#layers.findIndex((l) => l.id === id);
    if (fromIdx === -1) throw new Error(`Layer "${id}" not found.`);

    const [entry] = this.#layers.splice(fromIdx, 1);

    if (beforeId) {
      const toIdx = this.#layers.findIndex((l) => l.id === beforeId);
      if (toIdx === -1) throw new Error(`Target layer "${beforeId}" not found.`);
      this.#layers.splice(toIdx, 0, entry);
    } else {
      this.#layers.push(entry);
    }

    this.#emit({ type: "layer-moved", layerId: id, beforeId });
  }

  /** Get all layer IDs in render order. */
  get layerIds(): string[] {
    return this.#layers.map((l) => l.id);
  }

  /** Get all layers in render order. */
  get layers(): ReadonlyArray<HonuaLayerSpecification> {
    return this.#layers.map((l) => l.spec);
  }

  /** Get all layers that reference a given source. */
  getLayersForSource(sourceId: string): HonuaLayerSpecification[] {
    return this.#layers.filter((l) => l.spec.source === sourceId).map((l) => l.spec);
  }

  // ── Style serialization ─────────────────────────────────────

  /**
   * Produce a MapLibre-compatible style specification.
   *
   * Honua sources are included with their original custom `type` values.
   * Use this to feed into a MapLibre map via `map.setStyle(honuaMap.toStyle())`
   * alongside a MapLibre source-type plugin that handles the Honua types.
   */
  toStyle(options?: { name?: string }): HonuaStyleSpecification {
    const sources: Record<string, HonuaSourceSpecification | { type: string; [key: string]: unknown }> = {};
    for (const [name, { spec }] of this.#sources) {
      sources[name] = spec;
    }

    return {
      version: 8,
      name: options?.name,
      sources,
      layers: this.#layers.map((l) => ({ ...l.spec })),
    };
  }

  // ── Events ──────────────────────────────────────────────────

  /** Subscribe to map structure changes. Returns a remove handle. */
  on(listener: HonuaMapEventListener): { remove(): void } {
    this.#listeners.add(listener);
    return {
      remove: () => {
        this.#listeners.delete(listener);
      },
    };
  }

  // ── Bulk operations ─────────────────────────────────────────

  /** Number of registered sources. */
  get sourceCount(): number {
    return this.#sources.size;
  }

  /** Number of registered layers. */
  get layerCount(): number {
    return this.#layers.length;
  }

  /** Remove all sources and layers. */
  clear(): void {
    const layerIds = [...this.layerIds];
    const sourceIds = [...this.sourceIds];
    this.#layers.length = 0;
    for (const id of layerIds) {
      this.#emit({ type: "layer-removed", layerId: id });
    }
    this.#sources.clear();
    for (const id of sourceIds) {
      this.#emit({ type: "source-removed", sourceId: id });
    }
  }

  // ── Private ─────────────────────────────────────────────────

  #loadStyle(style: HonuaStyleSpecification): void {
    for (const [name, spec] of Object.entries(style.sources)) {
      const resolved = this.#resolveSource(spec);
      this.#sources.set(name, { spec, resolved });
    }
    for (const layer of style.layers) {
      this.#layers.push({ id: layer.id, spec: layer });
    }
  }

  #resolveSource(spec: HonuaSourceSpecification | { type: string; [key: string]: unknown }): ResolvedMapSource {
    if (isFeatureServiceSource(spec)) {
      const parsed = parseFeatureLayerUrl(spec.url);
      return new HonuaFeatureLayer({
        client: this.#client,
        serviceId: parsed.serviceId,
        layerId: parsed.layerId,
      });
    }
    if (isMapServiceSource(spec)) {
      const parsed = parseMapServiceUrl(spec.url);
      return new HonuaMapService({
        client: this.#client,
        serviceId: parsed.serviceId,
      });
    }
    if (isOgcFeaturesSource(spec)) {
      const parsed = parseOgcFeaturesUrl(spec.url);
      const ogcRoot = new HonuaOgcFeatures({ client: this.#client });
      const collectionId = spec.collectionId ?? parsed.collectionId ?? "";
      return ogcRoot.collection(collectionId);
    }
    return null;
  }

  #emit(event: HonuaMapEvent): void {
    for (const listener of this.#listeners) {
      listener(event);
    }
  }
}
