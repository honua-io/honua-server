export interface MapCompatOptions {
  basemap?: unknown;
  layers?: unknown;
}

export class MapCompat {
  public basemap: unknown;
  private readonly layersInternal: unknown[];

  public constructor(options: MapCompatOptions = {}) {
    this.basemap = options.basemap;
    this.layersInternal = Array.isArray(options.layers) ? [...options.layers] : [];
  }

  public get layers(): readonly unknown[] {
    return this.layersInternal;
  }

  public get allLayers(): readonly unknown[] {
    return this.layersInternal;
  }

  public add(layer: unknown, index?: number): void {
    if (index === undefined) {
      this.layersInternal.push(layer);
      return;
    }

    const insertAt = normalizeInsertIndex(index, this.layersInternal.length);
    this.layersInternal.splice(insertAt, 0, layer);
  }

  public addMany(layers: readonly unknown[], index?: number): void {
    if (layers.length === 0) {
      return;
    }

    if (index === undefined) {
      this.layersInternal.push(...layers);
      return;
    }

    const insertAt = normalizeInsertIndex(index, this.layersInternal.length);
    this.layersInternal.splice(insertAt, 0, ...layers);
  }

  public remove(layer: unknown): boolean {
    const index = this.layersInternal.indexOf(layer);
    if (index < 0) {
      return false;
    }

    this.layersInternal.splice(index, 1);
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
    this.layersInternal.length = 0;
  }

  public reorder(layer: unknown, index: number): boolean {
    const existingIndex = this.layersInternal.indexOf(layer);
    if (existingIndex < 0) {
      return false;
    }

    this.layersInternal.splice(existingIndex, 1);
    const insertAt = normalizeInsertIndex(index, this.layersInternal.length);
    this.layersInternal.splice(insertAt, 0, layer);
    return true;
  }

  public findLayerById(id: string): unknown {
    for (const layer of this.layersInternal) {
      if (isLayerWithId(layer) && layer.id === id) {
        return layer;
      }
    }

    return undefined;
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

function normalizeInsertIndex(index: number, length: number): number {
  const sanitized = Number.isFinite(index) ? Math.trunc(index) : length;
  return Math.min(Math.max(sanitized, 0), length);
}
