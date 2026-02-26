import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";

export interface MapCompatOptions {
  basemap?: unknown;
  layers?: unknown;
  ground?: unknown;
  tables?: unknown[];
  portalItem?: unknown;
  spatialReference?: unknown;
  eventBus?: CompatEventBus;
}

export class MapCompat {
  public basemap: unknown;
  public ground: unknown;
  public tables: unknown[];
  public portalItem: unknown;
  public spatialReference: unknown;
  public readonly eventBus: CompatEventBus;
  private readonly layersInternal: unknown[];

  public constructor(options: MapCompatOptions = {}) {
    this.basemap = options.basemap;
    this.ground = options.ground;
    this.tables = Array.isArray(options.tables) ? [...options.tables] : [];
    this.portalItem = options.portalItem;
    this.spatialReference = options.spatialReference;
    this.layersInternal = Array.isArray(options.layers) ? [...options.layers] : [];
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.layers) ?? new CompatEventBus();
  }

  public get layers(): readonly unknown[] {
    return this.layersInternal;
  }

  public get allLayers(): readonly unknown[] {
    const flattened: unknown[] = [];
    for (const layer of this.layersInternal) {
      flattened.push(layer);
      flattened.push(...extractNestedLayers(layer));
    }
    return flattened;
  }

  public add(layer: unknown, index?: number): void {
    if (index === undefined) {
      this.layersInternal.push(layer);
      this.eventBus.emit("map.layer-added", { layer, index: this.layersInternal.length - 1 }, this);
      return;
    }

    const insertAt = normalizeInsertIndex(index, this.layersInternal.length);
    this.layersInternal.splice(insertAt, 0, layer);
    this.eventBus.emit("map.layer-added", { layer, index: insertAt }, this);
  }

  public addMany(layers: readonly unknown[], index?: number): void {
    if (layers.length === 0) {
      return;
    }

    if (index === undefined) {
      const startIndex = this.layersInternal.length;
      this.layersInternal.push(...layers);
      this.eventBus.emit("map.layers-added", { layers: [...layers], index: startIndex }, this);
      return;
    }

    const insertAt = normalizeInsertIndex(index, this.layersInternal.length);
    this.layersInternal.splice(insertAt, 0, ...layers);
    this.eventBus.emit("map.layers-added", { layers: [...layers], index: insertAt }, this);
  }

  public remove(layer: unknown): boolean {
    const index = this.layersInternal.indexOf(layer);
    if (index < 0) {
      return false;
    }

    this.layersInternal.splice(index, 1);
    this.eventBus.emit("map.layer-removed", { layer, index }, this);
    return true;
  }

  public removeMany(layers: readonly unknown[]): number {
    let removedCount = 0;
    for (const layer of layers) {
      if (this.remove(layer)) {
        removedCount += 1;
      }
    }
    return removedCount;
  }

  public removeAll(): void {
    const removedLayers = [...this.layersInternal];
    this.layersInternal.length = 0;
    this.eventBus.emit("map.layers-cleared", { layers: removedLayers }, this);
  }

  public reorder(layer: unknown, index: number): boolean {
    const existingIndex = this.layersInternal.indexOf(layer);
    if (existingIndex < 0) {
      return false;
    }

    this.layersInternal.splice(existingIndex, 1);
    const insertAt = normalizeInsertIndex(index, this.layersInternal.length);
    this.layersInternal.splice(insertAt, 0, layer);
    this.eventBus.emit("map.layer-reordered", { layer, fromIndex: existingIndex, toIndex: insertAt }, this);
    return true;
  }

  public findLayerById(id: string): unknown {
    for (const layer of this.allLayers) {
      if (isLayerWithId(layer) && layer.id === id) {
        return layer;
      }
    }

    return undefined;
  }

  public setBasemap(basemap: unknown): void {
    this.basemap = basemap;
    this.eventBus.emit("map.basemap-changed", { basemap }, this);
  }

  public setGround(ground: unknown): void {
    this.ground = ground;
    this.eventBus.emit("map.ground-changed", { ground }, this);
  }

  public setTables(tables: readonly unknown[]): void {
    this.tables = [...tables];
    this.eventBus.emit("map.tables-changed", { tables: this.tables }, this);
  }

  public setSpatialReference(spatialReference: unknown): void {
    this.spatialReference = spatialReference;
    this.eventBus.emit("map.spatial-reference-changed", { spatialReference }, this);
  }
}

interface LayerWithId {
  id: string;
}

function isLayerWithId(value: unknown): value is LayerWithId {
  return (
    typeof value === "object" &&
    value !== null &&
    "id" in value &&
    typeof value.id === "string"
  );
}

function extractNestedLayers(layer: unknown): unknown[] {
  if (!isLayerWithChildren(layer)) {
    return [];
  }

  const nested: unknown[] = [];
  for (const child of layer.layers) {
    nested.push(child);
    nested.push(...extractNestedLayers(child));
  }
  return nested;
}

interface LayerWithChildren {
  layers: readonly unknown[];
}

function isLayerWithChildren(value: unknown): value is LayerWithChildren {
  return typeof value === "object" && value !== null && "layers" in value && Array.isArray(value.layers);
}

function normalizeInsertIndex(index: number, length: number): number {
  const sanitized = Number.isFinite(index) ? Math.trunc(index) : length;
  return Math.min(Math.max(sanitized, 0), length);
}
