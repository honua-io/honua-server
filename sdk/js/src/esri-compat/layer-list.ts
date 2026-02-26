import { CompatEventBus, type CompatEventSubscription, resolveCompatEventBus } from "./event-bus.js";

export interface LayerListCompatOptions {
  view?: unknown;
  map?: unknown;
  eventBus?: CompatEventBus;
  includeHidden?: boolean;
  autoRefresh?: boolean;
}

export interface LayerListItemCompat {
  id: string | undefined;
  title: string;
  visible: boolean;
  layer: unknown;
  children: LayerListItemCompat[];
}

export class LayerListCompat {
  public readonly view: unknown;
  public readonly map: unknown;
  public readonly eventBus: CompatEventBus;
  public readonly includeHidden: boolean;
  public items: LayerListItemCompat[];

  private readonly autoRefresh: boolean;
  private subscriptions: CompatEventSubscription[];

  public constructor(options: LayerListCompatOptions = {}) {
    this.view = options.view;
    this.map = options.map ?? extractMapFromView(options.view);
    this.eventBus =
      options.eventBus ?? resolveCompatEventBus(this.view, this.map) ?? new CompatEventBus();
    this.includeHidden = options.includeHidden ?? false;
    this.autoRefresh = options.autoRefresh ?? true;
    this.items = [];
    this.subscriptions = [];

    if (this.autoRefresh) {
      this.subscriptions.push(this.eventBus.on("map.layer-added", () => this.refresh()));
      this.subscriptions.push(this.eventBus.on("map.layer-removed", () => this.refresh()));
      this.subscriptions.push(this.eventBus.on("map.layers-added", () => this.refresh()));
      this.subscriptions.push(this.eventBus.on("map.layers-cleared", () => this.refresh()));
      this.subscriptions.push(this.eventBus.on("map.layer-reordered", () => this.refresh()));
      this.subscriptions.push(this.eventBus.on("group-layer.layer-added", () => this.refresh()));
      this.subscriptions.push(this.eventBus.on("group-layer.layer-removed", () => this.refresh()));
      this.subscriptions.push(this.eventBus.on("group-layer.layers-added", () => this.refresh()));
      this.subscriptions.push(this.eventBus.on("group-layer.layers-cleared", () => this.refresh()));
      this.subscriptions.push(this.eventBus.on("layer.visibility-changed", () => this.refresh()));
    }
  }

  public async load(): Promise<LayerListCompat> {
    this.refresh();
    return this;
  }

  public async when(callback?: (widget: LayerListCompat) => void): Promise<LayerListCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public refresh(): readonly LayerListItemCompat[] {
    const rootLayers = getRootLayers(this.map);
    this.items = rootLayers
      .map((layer, index) => toLayerListItem(layer, this.includeHidden, index))
      .filter((item): item is LayerListItemCompat => item !== undefined);
    this.eventBus.emit("layer-list.updated", { itemCount: this.items.length }, this);
    return this.items;
  }

  public toggle(layerOrId: unknown | string, visible?: boolean): boolean {
    const layer = this.findLayer(layerOrId);
    if (!layer || !isLayerLike(layer)) {
      return false;
    }

    const nextVisible = visible ?? !toVisible(layer);
    layer.visible = nextVisible;
    this.eventBus.emit("layer.visibility-changed", { layerId: layer.id, visible: nextVisible }, this);
    this.refresh();
    return true;
  }

  public destroy(): void {
    for (const subscription of this.subscriptions.splice(0)) {
      subscription.remove();
    }
  }

  private findLayer(layerOrId: unknown | string): unknown {
    const rootLayers = getRootLayers(this.map);
    if (typeof layerOrId !== "string") {
      return findLayerByReference(rootLayers, layerOrId);
    }
    return findLayerById(rootLayers, layerOrId);
  }
}

function extractMapFromView(view: unknown): unknown {
  if (!isRecord(view)) {
    return undefined;
  }
  return view.map;
}

function getRootLayers(map: unknown): unknown[] {
  if (!isRecord(map) || !Array.isArray(map.layers)) {
    return [];
  }
  return [...map.layers];
}

function toLayerListItem(
  layer: unknown,
  includeHidden: boolean,
  index: number,
): LayerListItemCompat | undefined {
  if (!isLayerLike(layer)) {
    return undefined;
  }

  if (!includeHidden && !toVisible(layer)) {
    return undefined;
  }

  const children = getChildLayers(layer)
    .map((child, childIndex) => toLayerListItem(child, includeHidden, childIndex))
    .filter((item): item is LayerListItemCompat => item !== undefined);

  return {
    id: layer.id,
    title: toLayerTitle(layer, index),
    visible: toVisible(layer),
    layer,
    children,
  };
}

function findLayerByReference(layers: readonly unknown[], target: unknown): unknown {
  for (const layer of layers) {
    if (layer === target) {
      return layer;
    }
    const nested = findLayerByReference(getChildLayers(layer), target);
    if (nested !== undefined) {
      return nested;
    }
  }
  return undefined;
}

function findLayerById(layers: readonly unknown[], id: string): unknown {
  for (const layer of layers) {
    if (isLayerLike(layer) && layer.id === id) {
      return layer;
    }
    const nested = findLayerById(getChildLayers(layer), id);
    if (nested !== undefined) {
      return nested;
    }
  }
  return undefined;
}

interface LayerLike {
  id: string | undefined;
  title: string | undefined;
  visible: boolean | undefined;
  layers?: unknown[];
}

function isLayerLike(value: unknown): value is LayerLike {
  return typeof value === "object" && value !== null;
}

function getChildLayers(layer: unknown): unknown[] {
  if (!isRecord(layer) || !Array.isArray(layer.layers)) {
    return [];
  }
  return [...layer.layers];
}

function toLayerTitle(layer: LayerLike, index: number): string {
  if (typeof layer.title === "string" && layer.title.trim().length > 0) {
    return layer.title;
  }
  if (typeof layer.id === "string" && layer.id.trim().length > 0) {
    return layer.id;
  }
  return `Layer ${index + 1}`;
}

function toVisible(layer: LayerLike): boolean {
  return layer.visible ?? true;
}

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null;
}
