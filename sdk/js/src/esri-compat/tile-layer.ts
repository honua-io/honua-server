import { HonuaClient } from "../core/client.js";
import { CompatEventBus, resolveCompatEventBus, safeInvokeCompatListener } from "./event-bus.js";
import { parseMapServiceUrl } from "./url.js";

export interface TileLayerCompatOptions {
  url: string;
  id?: string;
  title?: string;
  opacity?: number;
  visible?: boolean;
  minScale?: number;
  maxScale?: number;
  listMode?: string;
  client?: HonuaClient;
  eventBus?: CompatEventBus;
}

export type TileLayerLoadStatusCompat = "not-loaded" | "loading" | "loaded" | "failed";

export interface TileLayerHandleCompat {
  remove(): void;
}

export class TileLayerCompat {
  public readonly url: string;
  public id: string;
  public title: string | undefined;
  public readonly serviceId: string;
  public opacity: number;
  public visible: boolean;
  public minScale: number;
  public maxScale: number;
  public listMode: string;
  public loaded: boolean;
  public loadStatus: TileLayerLoadStatusCompat;
  public metadata: unknown;
  public readonly eventBus: CompatEventBus;

  private readonly client: HonuaClient;
  private readonly baseUrl: string;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: TileLayerCompatOptions) {
    const parsed = parseMapServiceUrl(options.url);
    this.url = options.url;
    this.baseUrl = parsed.baseUrl;
    this.serviceId = parsed.serviceId;
    this.id = options.id ?? this.serviceId;
    this.title = options.title;
    this.opacity = options.opacity ?? 1;
    this.visible = options.visible ?? true;
    this.minScale = normalizeScale(options.minScale);
    this.maxScale = normalizeScale(options.maxScale);
    this.listMode = options.listMode ?? "show";
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.metadata = undefined;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.client) ?? new CompatEventBus();
    this.client = options.client ?? new HonuaClient({ baseUrl: parsed.baseUrl });
    this.watchListeners = new Map();
  }

  public async load(): Promise<TileLayerCompat> {
    if (!this.loaded) {
      this.loadStatus = "loading";
      this.notifyWatchers("loadStatus", this.loadStatus);
      this.eventBus.emit("tile-layer.loading", { serviceId: this.serviceId, id: this.id }, this);
      try {
        this.metadata = await this.client.getMapServiceMetadata(this.serviceId);
        this.notifyWatchers("metadata", this.metadata);
        this.loaded = true;
        this.notifyWatchers("loaded", this.loaded);
        this.loadStatus = "loaded";
        this.notifyWatchers("loadStatus", this.loadStatus);
        this.eventBus.emit("tile-layer.loaded", { serviceId: this.serviceId, id: this.id }, this);
      } catch (error) {
        this.metadata = undefined;
        this.notifyWatchers("metadata", this.metadata);
        this.loaded = false;
        this.notifyWatchers("loaded", this.loaded);
        this.loadStatus = "failed";
        this.notifyWatchers("loadStatus", this.loadStatus);
        this.eventBus.emit("tile-layer.failed", { serviceId: this.serviceId, id: this.id, error }, this);
        throw error;
      }
    }
    return this;
  }

  public async when(callback?: (layer: TileLayerCompat) => void): Promise<TileLayerCompat> {
    const layer = await this.load();
    if (callback) {
      callback(layer);
    }
    return layer;
  }

  public refresh(): void {
    this.loaded = false;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "not-loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.metadata = undefined;
    this.notifyWatchers("metadata", this.metadata);
    this.eventBus.emit("tile-layer.refreshed", { serviceId: this.serviceId, id: this.id }, this);
  }

  public watch(propertyName: string, listener: (value: unknown) => void): TileLayerHandleCompat {
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

  public setVisibility(visible: boolean): void {
    this.visible = visible;
    this.notifyWatchers("visible", this.visible);
    this.eventBus.emit("layer.visibility-changed", { layerId: this.id, visible }, this);
  }

  public setOpacity(opacity: number): void {
    this.opacity = normalizeOpacity(opacity);
    this.notifyWatchers("opacity", this.opacity);
    this.eventBus.emit("layer.opacity-changed", { layerId: this.id, opacity: this.opacity }, this);
  }

  public setScaleRange(minScale: number | undefined, maxScale: number | undefined): void {
    this.minScale = normalizeScale(minScale);
    this.maxScale = normalizeScale(maxScale);
    this.notifyWatchers("minScale", this.minScale);
    this.notifyWatchers("maxScale", this.maxScale);
    this.eventBus.emit(
      "tile-layer.scale-range-changed",
      { layerId: this.id, minScale: this.minScale, maxScale: this.maxScale },
      this,
    );
  }

  public setListMode(listMode: string): void {
    this.listMode = listMode;
    this.notifyWatchers("listMode", this.listMode);
    this.eventBus.emit("tile-layer.list-mode-changed", { layerId: this.id, listMode }, this);
  }

  public getTileUrl(level: number, row: number, col: number): string {
    return `${this.baseUrl}/rest/services/${encodeURIComponent(this.serviceId)}/MapServer/tile/${level}/${row}/${col}`;
  }

  private notifyWatchers(propertyName: string, value: unknown): void {
    const listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      safeInvokeCompatListener(listener, value);
    }
  }
}

function normalizeScale(scale: number | undefined): number {
  if (scale === undefined || !Number.isFinite(scale)) {
    return 0;
  }
  return Math.max(0, Math.trunc(scale));
}

function normalizeOpacity(opacity: number): number {
  if (!Number.isFinite(opacity)) {
    return 1;
  }
  return Math.min(Math.max(opacity, 0), 1);
}
