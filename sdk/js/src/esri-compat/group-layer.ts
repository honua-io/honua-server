import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";

export interface GroupLayerCompatOptions {
  id?: string;
  title?: string;
  layers?: unknown[];
  visible?: boolean;
  opacity?: number;
  listMode?: "show" | "hide";
  visibilityMode?: "independent" | "inherited" | "exclusive";
  eventBus?: CompatEventBus;
}

export class GroupLayerCompat {
  public readonly type: "group";
  public id: string | undefined;
  public title: string | undefined;
  public visible: boolean;
  public opacity: number;
  public listMode: "show" | "hide";
  public visibilityMode: "independent" | "inherited" | "exclusive";
  public readonly eventBus: CompatEventBus;
  private readonly layersInternal: unknown[];

  public constructor(options: GroupLayerCompatOptions = {}) {
    this.type = "group";
    this.id = options.id;
    this.title = options.title;
    this.visible = options.visible ?? true;
    this.opacity = options.opacity ?? 1;
    this.listMode = options.listMode ?? "show";
    this.visibilityMode = options.visibilityMode ?? "independent";
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
      this.eventBus.emit("group-layer.layer-added", { groupLayerId: this.id, layer, index: this.layersInternal.length - 1 }, this);
      return;
    }

    const insertAt = normalizeInsertIndex(index, this.layersInternal.length);
    this.layersInternal.splice(insertAt, 0, layer);
    this.eventBus.emit("group-layer.layer-added", { groupLayerId: this.id, layer, index: insertAt }, this);
  }

  public addMany(layers: readonly unknown[], index?: number): void {
    if (layers.length === 0) {
      return;
    }

    if (index === undefined) {
      const startIndex = this.layersInternal.length;
      this.layersInternal.push(...layers);
      this.eventBus.emit("group-layer.layers-added", { groupLayerId: this.id, layers: [...layers], index: startIndex }, this);
      return;
    }

    const insertAt = normalizeInsertIndex(index, this.layersInternal.length);
    this.layersInternal.splice(insertAt, 0, ...layers);
    this.eventBus.emit("group-layer.layers-added", { groupLayerId: this.id, layers: [...layers], index: insertAt }, this);
  }

  public remove(layer: unknown): boolean {
    const index = this.layersInternal.indexOf(layer);
    if (index < 0) {
      return false;
    }

    this.layersInternal.splice(index, 1);
    this.eventBus.emit("group-layer.layer-removed", { groupLayerId: this.id, layer, index }, this);
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
    this.eventBus.emit("group-layer.layers-cleared", { groupLayerId: this.id, layers: removedLayers }, this);
  }

  public findLayerById(id: string): unknown {
    for (const layer of this.allLayers) {
      if (isLayerWithId(layer) && layer.id === id) {
        return layer;
      }
    }

    return undefined;
  }

  public setVisibility(visible: boolean): void {
    this.visible = visible;
    this.eventBus.emit("layer.visibility-changed", { layerId: this.id, visible }, this);
  }

  public setOpacity(opacity: number): void {
    this.opacity = opacity;
    this.eventBus.emit("layer.opacity-changed", { layerId: this.id, opacity }, this);
  }
}

interface LayerWithId {
  id: string;
}

function isLayerWithId(value: unknown): value is LayerWithId {
  return typeof value === "object" && value !== null && "id" in value && typeof value.id === "string";
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
