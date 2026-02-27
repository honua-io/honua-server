import { CompatEventBus, type CompatEventSubscription, resolveCompatEventBus } from "./event-bus.js";

export interface BasemapGalleryCompatOptions {
  view?: unknown;
  map?: unknown;
  container?: unknown;
  source?: readonly unknown[];
  eventBus?: CompatEventBus;
  autoRefresh?: boolean;
}

export type BasemapGalleryLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface BasemapGalleryHandleCompat {
  remove(): void;
}

export class BasemapGalleryCompat {
  public readonly view: unknown;
  public readonly map: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public readonly autoRefresh: boolean;
  public loaded: boolean;
  public loadStatus: BasemapGalleryLoadStatusCompat;
  public source: unknown[];
  public activeBasemap: unknown;

  private readonly subscriptions: CompatEventSubscription[];
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: BasemapGalleryCompatOptions = {}) {
    this.view = options.view;
    this.map = options.map ?? extractViewMap(options.view);
    this.container = options.container;
    this.eventBus =
      options.eventBus ?? resolveCompatEventBus(options.view, this.map) ?? new CompatEventBus();
    this.autoRefresh = options.autoRefresh ?? true;
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.source = Array.isArray(options.source) ? [...options.source] : [];
    this.activeBasemap = extractMapBasemap(this.map);
    this.subscriptions = [];
    this.watchListeners = new Map();

    if (this.autoRefresh) {
      this.subscriptions.push(
        this.eventBus.on("map.basemap-changed", (event) => {
          this.activeBasemap = extractPayloadBasemap(event.payload);
          this.notifyWatchers("activeBasemap", this.activeBasemap);
          this.eventBus.emit(
            "basemap-gallery.active-basemap-changed",
            { basemap: this.activeBasemap },
            this,
          );
        }),
      );
    }
  }

  public async load(): Promise<BasemapGalleryCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("basemap-gallery.loading", undefined, this);
    this.refresh();
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("basemap-gallery.loaded", { basemapCount: this.source.length }, this);
    return this;
  }

  public async when(callback?: (widget: BasemapGalleryCompat) => void): Promise<BasemapGalleryCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): BasemapGalleryHandleCompat {
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

  public get basemaps(): readonly unknown[] {
    return this.source;
  }

  public setBasemaps(basemaps: readonly unknown[]): void {
    this.source = [...basemaps];
    this.notifyWatchers("source", this.source);
    this.eventBus.emit("basemap-gallery.updated", { basemapCount: this.source.length }, this);
  }

  public select(basemapOrId: unknown): unknown {
    const basemap = this.resolveBasemap(basemapOrId);
    if (basemap === undefined) {
      return undefined;
    }

    setMapBasemap(this.map, basemap, this.eventBus, this);
    this.activeBasemap = basemap;
    this.notifyWatchers("activeBasemap", this.activeBasemap);
    this.eventBus.emit("basemap-gallery.selected", { basemap }, this);
    this.eventBus.emit("basemap.toggle", { activeBasemap: basemap }, this);
    return basemap;
  }

  public refresh(): unknown {
    this.activeBasemap = extractMapBasemap(this.map);
    this.notifyWatchers("activeBasemap", this.activeBasemap);
    this.eventBus.emit(
      "basemap-gallery.active-basemap-changed",
      { basemap: this.activeBasemap },
      this,
    );
    return this.activeBasemap;
  }

  public destroy(): void {
    for (const subscription of this.subscriptions.splice(0)) {
      subscription.remove();
    }
    this.watchListeners.clear();
  }

  private resolveBasemap(basemapOrId: unknown): unknown {
    if (typeof basemapOrId !== "string") {
      return basemapOrId;
    }

    for (const candidate of this.source) {
      if (!isRecord(candidate)) {
        continue;
      }
      if (candidate.id === basemapOrId || candidate.title === basemapOrId) {
        return candidate;
      }
    }

    return undefined;
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

function extractViewMap(view: unknown): unknown {
  if (!isRecord(view)) {
    return undefined;
  }
  return view.map;
}

function extractMapBasemap(map: unknown): unknown {
  if (!isRecord(map)) {
    return undefined;
  }
  return map.basemap;
}

function extractPayloadBasemap(payload: unknown): unknown {
  if (!isRecord(payload)) {
    return undefined;
  }
  return payload.basemap;
}

function isMapBasemapSetter(value: unknown): value is MapBasemapSetter {
  return isRecord(value) && typeof value.setBasemap === "function";
}

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null;
}
