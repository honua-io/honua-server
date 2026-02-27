import { CompatEventBus, type CompatEventSubscription, resolveCompatEventBus } from "./event-bus.js";

export interface LegendCompatOptions {
  view?: unknown;
  map?: unknown;
  layers?: readonly unknown[];
  container?: unknown;
  eventBus?: CompatEventBus;
  includeHidden?: boolean;
  autoRefresh?: boolean;
}

export interface LegendItemCompat {
  layerId: number | undefined;
  layerName: string | undefined;
  label: string;
  imageData: string | undefined;
  contentType: string | undefined;
  width: number | undefined;
  height: number | undefined;
}

export interface LegendLayerGroupCompat {
  layer: unknown;
  title: string;
  entries: LegendItemCompat[];
}

export type LegendLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface LegendHandleCompat {
  remove(): void;
}

export class LegendCompat {
  public readonly view: unknown;
  public readonly map: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public readonly includeHidden: boolean;
  public loaded: boolean;
  public loadStatus: LegendLoadStatusCompat;
  public items: LegendLayerGroupCompat[];

  private readonly explicitLayers: readonly unknown[] | undefined;
  private readonly autoRefresh: boolean;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;
  private subscriptions: CompatEventSubscription[];
  private refreshSequence: number;

  public constructor(options: LegendCompatOptions = {}) {
    this.view = options.view;
    this.map = options.map ?? extractMapFromView(options.view);
    this.container = options.container;
    this.explicitLayers = options.layers;
    this.eventBus =
      options.eventBus ?? resolveCompatEventBus(this.view, this.map, options.layers) ?? new CompatEventBus();
    this.includeHidden = options.includeHidden ?? false;
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.autoRefresh = options.autoRefresh ?? true;
    this.items = [];
    this.watchListeners = new Map();
    this.subscriptions = [];
    this.refreshSequence = 0;

    if (this.autoRefresh) {
      this.subscriptions.push(this.eventBus.on("map.layer-added", () => void this.refresh()));
      this.subscriptions.push(this.eventBus.on("map.layer-removed", () => void this.refresh()));
      this.subscriptions.push(this.eventBus.on("map.layers-added", () => void this.refresh()));
      this.subscriptions.push(this.eventBus.on("map.layers-cleared", () => void this.refresh()));
      this.subscriptions.push(this.eventBus.on("map.layer-reordered", () => void this.refresh()));
      this.subscriptions.push(this.eventBus.on("group-layer.layer-added", () => void this.refresh()));
      this.subscriptions.push(this.eventBus.on("group-layer.layer-removed", () => void this.refresh()));
      this.subscriptions.push(this.eventBus.on("group-layer.layers-added", () => void this.refresh()));
      this.subscriptions.push(this.eventBus.on("group-layer.layers-cleared", () => void this.refresh()));
      this.subscriptions.push(this.eventBus.on("layer.visibility-changed", () => void this.refresh()));
      this.subscriptions.push(this.eventBus.on("layer.legend-updated", () => void this.refresh()));
    }
  }

  public async load(): Promise<LegendCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("legend.loading", undefined, this);
    await this.refresh();
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("legend.loaded", { layerCount: this.items.length }, this);
    return this;
  }

  public async when(callback?: (widget: LegendCompat) => void): Promise<LegendCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): LegendHandleCompat {
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

  public async refresh(): Promise<readonly LegendLayerGroupCompat[]> {
    const refreshId = ++this.refreshSequence;
    const layers = this.resolveLegendLayers();
    const groups: LegendLayerGroupCompat[] = [];
    for (const layer of layers) {
      if (!this.includeHidden && !isLayerVisible(layer)) {
        continue;
      }

      const entries = await extractLegendEntries(layer);
      if (entries.length === 0) {
        continue;
      }

      groups.push({
        layer,
        title: toLayerTitle(layer, groups.length),
        entries,
      });
    }

    if (refreshId !== this.refreshSequence) {
      return this.items;
    }

    this.items = groups;
    this.notifyWatchers("items", this.items);
    this.eventBus.emit("legend.updated", { layerCount: groups.length }, this);
    return this.items;
  }

  public destroy(): void {
    for (const subscription of this.subscriptions.splice(0)) {
      subscription.remove();
    }
    this.watchListeners.clear();
  }

  private resolveLegendLayers(): unknown[] {
    if (this.explicitLayers) {
      return [...this.explicitLayers];
    }

    return getRootLayers(this.map);
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

async function extractLegendEntries(layer: unknown): Promise<LegendItemCompat[]> {
  const response = await callLegendProvider(layer);
  if (!isRecord(response) || !Array.isArray(response.layers)) {
    return [];
  }

  const entries: LegendItemCompat[] = [];
  for (const layerEntry of response.layers) {
    if (!isRecord(layerEntry) || !Array.isArray(layerEntry.legend)) {
      continue;
    }

    for (const legendEntry of layerEntry.legend) {
      if (!isRecord(legendEntry)) {
        continue;
      }

      const label = typeof legendEntry.label === "string" ? legendEntry.label : "Legend";
      entries.push({
        layerId: toOptionalNumber(layerEntry.layerId),
        layerName: toOptionalString(layerEntry.layerName),
        label,
        imageData: toOptionalString(legendEntry.imageData),
        contentType: toOptionalString(legendEntry.contentType),
        width: toOptionalNumber(legendEntry.width),
        height: toOptionalNumber(legendEntry.height),
      });
    }
  }

  return entries;
}

async function callLegendProvider(layer: unknown): Promise<unknown> {
  if (hasMethod(layer, "getLegend")) {
    return layer.getLegend();
  }
  if (hasMethod(layer, "legend")) {
    return layer.legend();
  }
  return undefined;
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

function isLayerVisible(layer: unknown): boolean {
  if (!isRecord(layer) || typeof layer.visible !== "boolean") {
    return true;
  }
  return layer.visible;
}

function toLayerTitle(layer: unknown, index: number): string {
  if (!isRecord(layer)) {
    return `Layer ${index + 1}`;
  }
  if (typeof layer.title === "string" && layer.title.trim().length > 0) {
    return layer.title;
  }
  if (typeof layer.id === "string" && layer.id.trim().length > 0) {
    return layer.id;
  }
  return `Layer ${index + 1}`;
}

interface LegendProvider {
  getLegend(): Promise<unknown> | unknown;
  legend(): Promise<unknown> | unknown;
}

function hasMethod<TName extends keyof LegendProvider>(
  value: unknown,
  methodName: TName,
): value is Pick<LegendProvider, TName> {
  return isRecord(value) && typeof value[methodName] === "function";
}

function toOptionalString(value: unknown): string | undefined {
  return typeof value === "string" ? value : undefined;
}

function toOptionalNumber(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null;
}
