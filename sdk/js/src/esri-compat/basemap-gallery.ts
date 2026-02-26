import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";

export interface BasemapGalleryCompatOptions {
  view?: unknown;
  map?: unknown;
  container?: unknown;
  source?: readonly unknown[];
  eventBus?: CompatEventBus;
}

export class BasemapGalleryCompat {
  public readonly view: unknown;
  public readonly map: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public source: unknown[];
  public activeBasemap: unknown;

  public constructor(options: BasemapGalleryCompatOptions = {}) {
    this.view = options.view;
    this.map = options.map ?? extractViewMap(options.view);
    this.container = options.container;
    this.eventBus =
      options.eventBus ?? resolveCompatEventBus(options.view, options.map) ?? new CompatEventBus();
    this.source = Array.isArray(options.source) ? [...options.source] : [];
    this.activeBasemap = extractMapBasemap(this.map);
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

    this.activeBasemap = basemap;
    if (isRecord(this.map)) {
      this.map.basemap = basemap;
    }
    this.eventBus.emit("basemap-gallery.selected", { basemap }, this);
    this.eventBus.emit("basemap.toggle", { activeBasemap: basemap }, this);
    return basemap;
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

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null;
}
