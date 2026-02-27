import { CompatEventBus, type CompatEventSubscription, resolveCompatEventBus } from "./event-bus.js";

export interface BasemapGalleryCompatOptions {
  view?: unknown;
  map?: unknown;
  container?: unknown;
  source?: readonly unknown[];
  eventBus?: CompatEventBus;
  autoRefresh?: boolean;
}

export class BasemapGalleryCompat {
  public readonly view: unknown;
  public readonly map: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public readonly autoRefresh: boolean;
  public source: unknown[];
  public activeBasemap: unknown;

  private readonly subscriptions: CompatEventSubscription[];

  public constructor(options: BasemapGalleryCompatOptions = {}) {
    this.view = options.view;
    this.map = options.map ?? extractViewMap(options.view);
    this.container = options.container;
    this.eventBus =
      options.eventBus ?? resolveCompatEventBus(options.view, this.map) ?? new CompatEventBus();
    this.autoRefresh = options.autoRefresh ?? true;
    this.source = Array.isArray(options.source) ? [...options.source] : [];
    this.activeBasemap = extractMapBasemap(this.map);
    this.subscriptions = [];

    if (this.autoRefresh) {
      this.subscriptions.push(
        this.eventBus.on("map.basemap-changed", (event) => {
          this.activeBasemap = extractPayloadBasemap(event.payload);
          this.eventBus.emit(
            "basemap-gallery.active-basemap-changed",
            { basemap: this.activeBasemap },
            this,
          );
        }),
      );
    }
  }

  public get basemaps(): readonly unknown[] {
    return this.source;
  }

  public setBasemaps(basemaps: readonly unknown[]): void {
    this.source = [...basemaps];
    this.eventBus.emit("basemap-gallery.updated", { basemapCount: this.source.length }, this);
  }

  public select(basemapOrId: unknown): unknown {
    const basemap = this.resolveBasemap(basemapOrId);
    if (basemap === undefined) {
      return undefined;
    }

    setMapBasemap(this.map, basemap, this.eventBus, this);
    this.activeBasemap = basemap;
    this.eventBus.emit("basemap-gallery.selected", { basemap }, this);
    this.eventBus.emit("basemap.toggle", { activeBasemap: basemap }, this);
    return basemap;
  }

  public refresh(): unknown {
    this.activeBasemap = extractMapBasemap(this.map);
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
