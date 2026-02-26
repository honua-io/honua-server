import { HonuaClient } from "../core/client.js";
import type { ExportMapRequest, MapIdentifyRequest, MapLegendRequest } from "../core/types.js";
import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";
import { parseMapServiceUrl } from "./url.js";

export interface MapImageLayerCompatOptions {
  url: string;
  sublayers?: unknown[];
  opacity?: number;
  visible?: boolean;
  client?: HonuaClient;
  eventBus?: CompatEventBus;
}

export interface MapImageLayerExportOptions extends Omit<ExportMapRequest, "serviceId"> {}
export interface MapImageLayerLegendOptions extends Omit<MapLegendRequest, "serviceId"> {}
export interface MapImageLayerIdentifyOptions extends Omit<MapIdentifyRequest, "serviceId"> {}

export class MapImageLayerCompat {
  public readonly url: string;
  public readonly serviceId: string;
  public sublayers: unknown[];
  public opacity: number;
  public visible: boolean;
  public loaded: boolean;
  public metadata: unknown;
  public readonly eventBus: CompatEventBus;

  private readonly client: HonuaClient;

  public constructor(options: MapImageLayerCompatOptions) {
    const parsed = parseMapServiceUrl(options.url);
    this.url = options.url;
    this.serviceId = parsed.serviceId;
    this.sublayers = Array.isArray(options.sublayers) ? [...options.sublayers] : [];
    this.opacity = options.opacity ?? 1;
    this.visible = options.visible ?? true;
    this.loaded = false;
    this.metadata = undefined;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.client, options.sublayers) ?? new CompatEventBus();
    this.client = options.client ?? new HonuaClient({ baseUrl: parsed.baseUrl });
  }

  public async load(): Promise<MapImageLayerCompat> {
    if (!this.loaded) {
      this.metadata = await this.client.getMapServiceMetadata(this.serviceId);
      this.eventBus.emit("map-image-layer.loaded", { serviceId: this.serviceId }, this);
    }
    this.loaded = true;
    return this;
  }

  public async when(callback?: (layer: MapImageLayerCompat) => void): Promise<MapImageLayerCompat> {
    const layer = await this.load();
    if (callback) {
      callback(layer);
    }

    return layer;
  }

  public refresh(): void {
    this.loaded = false;
    this.metadata = undefined;
    this.eventBus.emit("map-image-layer.refreshed", { serviceId: this.serviceId }, this);
  }

  public exportImage(options: MapImageLayerExportOptions): Promise<unknown> {
    return this.client.exportMap({
      ...options,
      serviceId: this.serviceId,
    });
  }

  public getLegend(options: MapImageLayerLegendOptions = {}): Promise<unknown> {
    return this.client.getMapLegend({
      ...options,
      serviceId: this.serviceId,
    });
  }

  public legend(options: MapImageLayerLegendOptions = {}): Promise<unknown> {
    return this.getLegend(options);
  }

  public identify(options: MapImageLayerIdentifyOptions): Promise<unknown> {
    return this.client.identifyMap({
      ...options,
      serviceId: this.serviceId,
    });
  }

  public setVisibility(visible: boolean): void {
    this.visible = visible;
    this.eventBus.emit("layer.visibility-changed", { layerId: this.serviceId, visible }, this);
  }
}
