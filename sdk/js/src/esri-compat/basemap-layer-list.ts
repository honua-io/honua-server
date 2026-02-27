import { CompatEventBus, type CompatEventSubscription, resolveCompatEventBus } from "./event-bus.js";

export interface BasemapLayerListCompatOptions {
  view?: unknown;
  map?: unknown;
  container?: unknown;
  eventBus?: CompatEventBus;
  autoRefresh?: boolean;
}

export type BasemapLayerListLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface BasemapLayerListHandleCompat {
  remove(): void;
}

export class BasemapLayerListCompat {
  public readonly view: unknown;
  public readonly map: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public readonly autoRefresh: boolean;
  public loaded: boolean;
  public loadStatus: BasemapLayerListLoadStatusCompat;
  public basemap: unknown;
  public baseLayers: unknown[];
  public referenceLayers: unknown[];

  private readonly subscriptions: CompatEventSubscription[];
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: BasemapLayerListCompatOptions = {}) {
    this.view = options.view;
    this.map = options.map ?? extractViewMap(options.view);
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view, options.map) ?? new CompatEventBus();
    this.autoRefresh = options.autoRefresh ?? true;
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.basemap = undefined;
    this.baseLayers = [];
    this.referenceLayers = [];
    this.subscriptions = [];
    this.watchListeners = new Map();
    this.refresh();

    if (this.autoRefresh) {
      this.subscriptions.push(this.eventBus.on("map.basemap-changed", () => this.refresh()));
      this.subscriptions.push(this.eventBus.on("basemap.toggle", () => this.refresh()));
    }
  }

  public async load(): Promise<BasemapLayerListCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("basemap-layer-list.loading", undefined, this);
    this.refresh();
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("basemap-layer-list.loaded", undefined, this);
    return this;
  }

  public async when(
    callback?: (widget: BasemapLayerListCompat) => void,
  ): Promise<BasemapLayerListCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(
    propertyName: string,
    listener: (value: unknown) => void,
  ): BasemapLayerListHandleCompat {
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

  public refresh(): readonly unknown[] {
    const basemap = extractMapBasemap(this.map);
    this.basemap = basemap;
    this.notifyWatchers("basemap", this.basemap);
    this.baseLayers = extractBasemapLayerCollection(basemap, "baseLayers");
    this.notifyWatchers("baseLayers", this.baseLayers);
    this.referenceLayers = extractBasemapLayerCollection(basemap, "referenceLayers");
    this.notifyWatchers("referenceLayers", this.referenceLayers);
    this.eventBus.emit(
      "basemap-layer-list.refreshed",
      {
        basemap: this.basemap,
        baseLayerCount: this.baseLayers.length,
        referenceLayerCount: this.referenceLayers.length,
      },
      this,
    );
    return [...this.baseLayers, ...this.referenceLayers];
  }

  public setBasemap(basemap: unknown): void {
    setMapBasemap(this.map, basemap, this.eventBus, this);
    this.refresh();
  }

  public destroy(): void {
    for (const subscription of this.subscriptions.splice(0)) {
      subscription.remove();
    }
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

interface MapBasemapSetter {
  setBasemap(basemap: unknown): void;
}

function setMapBasemap(map: unknown, basemap: unknown, eventBus: CompatEventBus, source: unknown): void {
  if (!isRecord(map)) {
    return;
  }
  if (isMapBasemapSetter(map)) {
    map.setBasemap(basemap);
    return;
  }

  map.basemap = basemap;
  eventBus.emit("map.basemap-changed", { basemap }, source);
}

function extractMapBasemap(map: unknown): unknown {
  if (!isRecord(map)) {
    return undefined;
  }
  return map.basemap;
}

function extractBasemapLayerCollection(
  basemap: unknown,
  key: "baseLayers" | "referenceLayers",
): unknown[] {
  if (!isRecord(basemap)) {
    return [];
  }
  const value = basemap[key];
  return Array.isArray(value) ? [...value] : [];
}

function extractViewMap(view: unknown): unknown {
  if (!isRecord(view)) {
    return undefined;
  }
  return view.map;
}

function isMapBasemapSetter(value: unknown): value is MapBasemapSetter {
  return isRecord(value) && typeof value.setBasemap === "function";
}

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null;
}
