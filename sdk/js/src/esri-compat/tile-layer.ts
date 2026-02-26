import { HonuaClient } from "../core/client.js";
import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";
import { parseMapServiceUrl } from "./url.js";

export interface TileLayerCompatOptions {
  url: string;
  opacity?: number;
  visible?: boolean;
  client?: HonuaClient;
  eventBus?: CompatEventBus;
}

export class TileLayerCompat {
  public readonly url: string;
  public readonly serviceId: string;
  public opacity: number;
  public visible: boolean;
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
    this.opacity = options.opacity ?? 1;
    this.visible = options.visible ?? true;
    this.loaded = false;
    this.metadata = undefined;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.client) ?? new CompatEventBus();
    this.client = options.client ?? new HonuaClient({ baseUrl: parsed.baseUrl });
  }

  public async load(): Promise<TileLayerCompat> {
    if (!this.loaded) {
      this.metadata = await this.client.getMapServiceMetadata(this.serviceId);
      this.eventBus.emit("tile-layer.loaded", { serviceId: this.serviceId }, this);
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
    this.eventBus.emit("tile-layer.refreshed", { serviceId: this.serviceId }, this);
  }

  public setVisibility(visible: boolean): void {
    this.visible = visible;
    this.eventBus.emit("layer.visibility-changed", { layerId: this.serviceId, visible }, this);
  }

  public setOpacity(opacity: number): void {
    this.opacity = opacity;
    this.eventBus.emit("layer.opacity-changed", { layerId: this.serviceId, opacity }, this);
  }

  public getTileUrl(level: number, row: number, col: number): string {
    return `${this.baseUrl}/rest/services/${encodeURIComponent(this.serviceId)}/MapServer/tile/${level}/${row}/${col}`;
  }
}
