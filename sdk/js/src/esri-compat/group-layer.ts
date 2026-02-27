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

export type GroupLayerLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface GroupLayerHandleCompat {
  remove(): void;
}

export class GroupLayerCompat {
  public readonly type: "group";
  public id: string | undefined;
  public title: string | undefined;
  public visible: boolean;
  public opacity: number;
  public listMode: "show" | "hide";
  public visibilityMode: "independent" | "inherited" | "exclusive";
  public loaded: boolean;
  public loadStatus: GroupLayerLoadStatusCompat;
  public readonly eventBus: CompatEventBus;
  private readonly layersInternal: unknown[];
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: GroupLayerCompatOptions = {}) {
    this.type = "group";
    this.id = options.id;
    this.title = options.title;
    this.visible = options.visible ?? true;
    this.opacity = options.opacity ?? 1;
    this.listMode = options.listMode ?? "show";
    this.visibilityMode = options.visibilityMode ?? "independent";
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.layersInternal = Array.isArray(options.layers) ? [...options.layers] : [];
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.layers) ?? new CompatEventBus();
    this.watchListeners = new Map();
  }

  public async load(): Promise<GroupLayerCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("group-layer.loading", { layerId: this.id }, this);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("group-layer.loaded", { layerId: this.id }, this);
    return this;
  }

  public async when(callback?: (layer: GroupLayerCompat) => void): Promise<GroupLayerCompat> {
    const layer = await this.load();
    if (callback) {
      callback(layer);
    }
    return layer;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): GroupLayerHandleCompat {
    let listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      listeners = new Set();
      this.watchListeners.set(propertyName, listeners);
    }
    listeners.add(listener);

    return {
      remove: () => {
        listeners?.delete(listener);
      },
    };
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
      this.notifyWatchers("layers", this.layers);
      this.notifyWatchers("allLayers", this.allLayers);
      this.eventBus.emit("group-layer.layer-added", { groupLayerId: this.id, layer, index: this.layersInternal.length - 1 }, this);
      return;
    }

    const insertAt = normalizeInsertIndex(index, this.layersInternal.length);
    this.layersInternal.splice(insertAt, 0, layer);
    this.notifyWatchers("layers", this.layers);
    this.notifyWatchers("allLayers", this.allLayers);
    this.eventBus.emit("group-layer.layer-added", { groupLayerId: this.id, layer, index: insertAt }, this);
  }

  public addMany(layers: readonly unknown[], index?: number): void {
    if (layers.length === 0) {
      return;
    }

    if (index === undefined) {
      const startIndex = this.layersInternal.length;
      this.layersInternal.push(...layers);
      this.notifyWatchers("layers", this.layers);
      this.notifyWatchers("allLayers", this.allLayers);
      this.eventBus.emit("group-layer.layers-added", { groupLayerId: this.id, layers: [...layers], index: startIndex }, this);
      return;
    }

    const insertAt = normalizeInsertIndex(index, this.layersInternal.length);
    this.layersInternal.splice(insertAt, 0, ...layers);
    this.notifyWatchers("layers", this.layers);
    this.notifyWatchers("allLayers", this.allLayers);
    this.eventBus.emit("group-layer.layers-added", { groupLayerId: this.id, layers: [...layers], index: insertAt }, this);
  }

  public remove(layer: unknown): boolean {
    const index = this.layersInternal.indexOf(layer);
    if (index < 0) {
      return false;
    }

    this.layersInternal.splice(index, 1);
    this.notifyWatchers("layers", this.layers);
    this.notifyWatchers("allLayers", this.allLayers);
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
    this.notifyWatchers("layers", this.layers);
    this.notifyWatchers("allLayers", this.allLayers);
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
    this.notifyWatchers("visible", this.visible);
    this.eventBus.emit("layer.visibility-changed", { layerId: this.id, visible }, this);
  }

  public setOpacity(opacity: number): void {
    this.opacity = opacity;
    this.notifyWatchers("opacity", this.opacity);
    this.eventBus.emit("layer.opacity-changed", { layerId: this.id, opacity }, this);
  }

  public destroy(): void {
    this.watchListeners.clear();
  }

  private notifyWatchers(propertyName: string, value: unknown): void {
    const listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      listener(value);
    }
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
