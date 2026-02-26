import { HonuaClient } from "../core/client.js";
import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";
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
  public metadata: unknown;
  public readonly eventBus: CompatEventBus;

  private readonly client: HonuaClient;
  private readonly baseUrl: string;

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
    this.metadata = undefined;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.client) ?? new CompatEventBus();
    this.client = options.client ?? new HonuaClient({ baseUrl: parsed.baseUrl });
  }

  public async load(): Promise<TileLayerCompat> {
    if (!this.loaded) {
      this.metadata = await this.client.getMapServiceMetadata(this.serviceId);
      this.eventBus.emit("tile-layer.loaded", { serviceId: this.serviceId, id: this.id }, this);
    }
    this.loaded = true;
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
    this.metadata = undefined;
    this.eventBus.emit("tile-layer.refreshed", { serviceId: this.serviceId, id: this.id }, this);
  }

  public setVisibility(visible: boolean): void {
    this.visible = visible;
    this.eventBus.emit("layer.visibility-changed", { layerId: this.id, visible }, this);
  }

  public setOpacity(opacity: number): void {
    this.opacity = opacity;
    this.eventBus.emit("layer.opacity-changed", { layerId: this.id, opacity }, this);
  }

  public setScaleRange(minScale: number | undefined, maxScale: number | undefined): void {
    this.minScale = normalizeScale(minScale);
    this.maxScale = normalizeScale(maxScale);
    this.eventBus.emit(
      "tile-layer.scale-range-changed",
      { layerId: this.id, minScale: this.minScale, maxScale: this.maxScale },
      this,
    );
  }

  public setListMode(listMode: string): void {
    this.listMode = listMode;
    this.eventBus.emit("tile-layer.list-mode-changed", { layerId: this.id, listMode }, this);
  }

  public getTileUrl(level: number, row: number, col: number): string {
    return `${this.baseUrl}/rest/services/${encodeURIComponent(this.serviceId)}/MapServer/tile/${level}/${row}/${col}`;
  }
}

function normalizeScale(scale: number | undefined): number {
  if (scale === undefined || !Number.isFinite(scale)) {
    return 0;
  }
  return Math.max(0, Math.trunc(scale));
}
