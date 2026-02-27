import { HonuaClient } from "../core/client.js";
import type {
  ExportMapRequest,
  MapFindRequest,
  MapIdentifyRequest,
  MapLegendRequest,
} from "../core/types.js";
import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";
import { parseMapServiceUrl } from "./url.js";

export interface MapImageLayerCompatOptions {
  url: string;
  id?: string;
  title?: string;
  sublayers?: unknown[];
  opacity?: number;
  visible?: boolean;
  minScale?: number;
  maxScale?: number;
  listMode?: string;
  legendEnabled?: boolean;
  client?: HonuaClient;
  eventBus?: CompatEventBus;
}

export interface MapImageLayerExportOptions extends Omit<ExportMapRequest, "serviceId"> {}
export interface MapImageLayerLegendOptions extends Omit<MapLegendRequest, "serviceId"> {}
export interface MapImageLayerIdentifyOptions extends Omit<MapIdentifyRequest, "serviceId"> {}
export interface MapImageLayerFindOptions extends Omit<MapFindRequest, "serviceId"> {}
export type MapImageLayerSublayerLookupId = number | string;

export type MapImageLayerLoadStatusCompat = "not-loaded" | "loading" | "loaded" | "failed";

export interface MapImageLayerHandleCompat {
  remove(): void;
}

export class MapImageLayerCompat {
  public readonly url: string;
  public id: string;
  public title: string | undefined;
  public readonly serviceId: string;
  public sublayers: unknown[];
  public opacity: number;
  public visible: boolean;
  public minScale: number;
  public maxScale: number;
  public listMode: string;
  public legendEnabled: boolean;
  public loaded: boolean;
  public loadStatus: MapImageLayerLoadStatusCompat;
  public metadata: unknown;
  public readonly eventBus: CompatEventBus;

  private readonly client: HonuaClient;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: MapImageLayerCompatOptions) {
    const parsed = parseMapServiceUrl(options.url);
    this.url = options.url;
    this.serviceId = parsed.serviceId;
    this.id = options.id ?? this.serviceId;
    this.title = options.title;
    this.sublayers = Array.isArray(options.sublayers) ? [...options.sublayers] : [];
    this.opacity = options.opacity ?? 1;
    this.visible = options.visible ?? true;
    this.minScale = normalizeScale(options.minScale);
    this.maxScale = normalizeScale(options.maxScale);
    this.listMode = options.listMode ?? "show";
    this.legendEnabled = options.legendEnabled ?? true;
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.metadata = undefined;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.client, options.sublayers) ?? new CompatEventBus();
    this.client = options.client ?? new HonuaClient({ baseUrl: parsed.baseUrl });
    this.watchListeners = new Map();
  }

  public async load(): Promise<MapImageLayerCompat> {
    if (!this.loaded) {
      this.loadStatus = "loading";
      this.notifyWatchers("loadStatus", this.loadStatus);
      this.eventBus.emit("map-image-layer.loading", { serviceId: this.serviceId, id: this.id }, this);
      try {
        this.metadata = await this.client.getMapServiceMetadata(this.serviceId);
        this.notifyWatchers("metadata", this.metadata);
        this.loaded = true;
        this.notifyWatchers("loaded", this.loaded);
        this.loadStatus = "loaded";
        this.notifyWatchers("loadStatus", this.loadStatus);
        this.eventBus.emit("map-image-layer.loaded", { serviceId: this.serviceId, id: this.id }, this);
      } catch (error) {
        this.metadata = undefined;
        this.notifyWatchers("metadata", this.metadata);
        this.loaded = false;
        this.notifyWatchers("loaded", this.loaded);
        this.loadStatus = "failed";
        this.notifyWatchers("loadStatus", this.loadStatus);
        this.eventBus.emit("map-image-layer.failed", { serviceId: this.serviceId, id: this.id, error }, this);
        throw error;
      }
    }
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
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "not-loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.metadata = undefined;
    this.notifyWatchers("metadata", this.metadata);
    this.eventBus.emit("map-image-layer.refreshed", { serviceId: this.serviceId, id: this.id }, this);
  }

  public watch(propertyName: string, listener: (value: unknown) => void): MapImageLayerHandleCompat {
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

  public get allSublayers(): readonly unknown[] {
    return [...this.sublayers];
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

  public find(options: MapImageLayerFindOptions): Promise<unknown> {
    return this.client.findMap({
      ...options,
      serviceId: this.serviceId,
    });
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

  public setSublayers(sublayers: readonly unknown[]): void {
    this.sublayers = [...sublayers];
    this.notifyWatchers("sublayers", this.sublayers);
    this.eventBus.emit("map-image-layer.sublayers-changed", { layerId: this.id }, this);
  }

  public findSublayerById(id: MapImageLayerSublayerLookupId): unknown {
    const expectedId = normalizeSublayerId(id);
    if (expectedId === undefined) {
      return undefined;
    }

    return this.sublayers.find((sublayer) => extractSublayerId(sublayer) === expectedId);
  }

  public setScaleRange(minScale: number | undefined, maxScale: number | undefined): void {
    this.minScale = normalizeScale(minScale);
    this.maxScale = normalizeScale(maxScale);
    this.notifyWatchers("minScale", this.minScale);
    this.notifyWatchers("maxScale", this.maxScale);
    this.eventBus.emit(
      "map-image-layer.scale-range-changed",
      { layerId: this.id, minScale: this.minScale, maxScale: this.maxScale },
      this,
    );
  }

  public setListMode(listMode: string): void {
    this.listMode = listMode;
    this.notifyWatchers("listMode", this.listMode);
    this.eventBus.emit("map-image-layer.list-mode-changed", { layerId: this.id, listMode }, this);
  }

  public setLegendEnabled(legendEnabled: boolean): void {
    this.legendEnabled = legendEnabled;
    this.notifyWatchers("legendEnabled", this.legendEnabled);
    this.eventBus.emit(
      "map-image-layer.legend-enabled-changed",
      { layerId: this.id, legendEnabled },
      this,
    );
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

function normalizeSublayerId(id: MapImageLayerSublayerLookupId): number | undefined {
  const parsed = Number(id);
  if (!Number.isFinite(parsed)) {
    return undefined;
  }
  return Math.trunc(parsed);
}

function extractSublayerId(sublayer: unknown): number | undefined {
  if (typeof sublayer !== "object" || sublayer === null) {
    return undefined;
  }
  const id = (sublayer as { id?: unknown }).id;
  if (id === undefined) {
    return undefined;
  }
  return normalizeSublayerId(id as MapImageLayerSublayerLookupId);
}
