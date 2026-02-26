import { HonuaClient } from "../core/client.js";
import type { ExportMapRequest } from "../core/types.js";
import { parseMapServiceUrl } from "./url.js";

export interface MapImageLayerCompatOptions {
  url: string;
  sublayers?: unknown[];
  opacity?: number;
  visible?: boolean;
  client?: HonuaClient;
}

export interface MapImageLayerExportOptions extends Omit<ExportMapRequest, "serviceId"> {}

export class MapImageLayerCompat {
  public readonly url: string;
  public readonly serviceId: string;
  public sublayers: unknown[];
  public opacity: number;
  public visible: boolean;
  public loaded: boolean;
  public metadata: unknown;

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
    this.client = options.client ?? new HonuaClient({ baseUrl: parsed.baseUrl });
  }

  public async load(): Promise<MapImageLayerCompat> {
    if (!this.loaded) {
      this.metadata = await this.client.getMapServiceMetadata(this.serviceId);
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
  }

  public exportImage(options: MapImageLayerExportOptions): Promise<unknown> {
    return this.client.exportMap({
      ...options,
      serviceId: this.serviceId,
    });
  }
}
