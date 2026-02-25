import { HonuaClient } from "../core/client.js";
import type { QueryMethod } from "../core/types.js";
import { parseFeatureLayerUrl } from "./url.js";

export interface FeatureLayerCompatOptions {
  url: string;
  client?: HonuaClient;
}

export interface FeatureLayerQueryOptions {
  where?: string;
  outFields?: string | string[];
  returnGeometry?: boolean;
  method?: QueryMethod;
  extraParams?: Record<string, string | number | boolean>;
}

export interface FeatureLayerEditsOptions {
  adds?: unknown[];
  updates?: unknown[];
  deletes?: number[] | string;
  rollbackOnFailure?: boolean;
}

export interface FeatureLayerCreateQueryResult {
  where: string;
  outFields: string[];
  returnGeometry: boolean;
}

export class FeatureLayerCompat {
  public readonly url: string;
  public readonly serviceId: string;
  public readonly layerId: number;
  public loaded: boolean;
  public metadata: unknown;

  private readonly client: HonuaClient;

  public constructor(options: FeatureLayerCompatOptions) {
    const parsed = parseFeatureLayerUrl(options.url);
    this.url = options.url;
    this.serviceId = parsed.serviceId;
    this.layerId = parsed.layerId;
    this.loaded = false;
    this.metadata = undefined;
    this.client = options.client ?? new HonuaClient({ baseUrl: parsed.baseUrl });
  }

  public async load(): Promise<FeatureLayerCompat> {
    if (!this.loaded) {
      this.metadata = await this.client.getLayerMetadata(this.serviceId, this.layerId);
    }
    this.loaded = true;
    return this;
  }

  public async when(callback?: (layer: FeatureLayerCompat) => void): Promise<FeatureLayerCompat> {
    const layer = await this.load();
    if (callback) {
      callback(layer);
    }

    return layer;
  }

  public createQuery(): FeatureLayerCreateQueryResult {
    return {
      where: "1=1",
      outFields: ["*"],
      returnGeometry: true,
    };
  }

  public queryFeatures(options: FeatureLayerQueryOptions = {}): Promise<unknown> {
    return this.client.queryFeatures({
      serviceId: this.serviceId,
      layerId: this.layerId,
      where: options.where,
      outFields: options.outFields,
      returnGeometry: options.returnGeometry,
      method: options.method,
      extraParams: options.extraParams,
    });
  }

  public applyEdits(options: FeatureLayerEditsOptions): Promise<unknown> {
    return this.client.applyEdits({
      serviceId: this.serviceId,
      layerId: this.layerId,
      adds: options.adds,
      updates: options.updates,
      deletes: options.deletes,
      rollbackOnFailure: options.rollbackOnFailure,
    });
  }
}
