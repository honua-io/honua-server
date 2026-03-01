import { HonuaClient } from "../core/client.js";
import type {
  ExportMapRequest,
  HonuaExportMapResponse,
  HonuaFieldInfo,
  HonuaFindResponse,
  HonuaIdentifyResponse,
  HonuaLegendResponse,
  HonuaQueryResponse,
  HonuaRelatedRecordsResponse,
  MapFindRequest,
  MapIdentifyRequest,
  MapLayerQueryRequest,
  MapLegendRequest,
  MapRelatedRecordsRequest,
} from "../core/types.js";
import { CompatEventBus, resolveCompatEventBus, safeInvokeCompatListener } from "./event-bus.js";
import { parseMapServiceUrl } from "./url.js";

export interface MapImageLayerCompatOptions {
  url: string;
  id?: string;
  title?: string;
  sublayers?: readonly Record<string, unknown>[];
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
export interface MapImageLayerQueryOptions extends Omit<MapLayerQueryRequest, "serviceId"> {}
export type MapImageLayerQueryAllOptions = MapImageLayerQueryOptions & {
  pageSize?: number;
  maxPages?: number;
};
export type MapImageLayerCreateQueryResult = MapImageLayerQueryOptions;
export type MapImageLayerQueryCountOptions = Pick<MapImageLayerQueryOptions, "layerId" | "where" | "method"> & {
  extraParams?: Record<string, string | number | boolean>;
};
export type MapImageLayerQueryObjectIdsOptions = Pick<MapImageLayerQueryOptions, "layerId" | "where" | "method"> & {
  extraParams?: Record<string, string | number | boolean>;
};
export type MapImageLayerQueryExtentOptions = Pick<MapImageLayerQueryOptions, "layerId" | "where" | "method"> & {
  extraParams?: Record<string, string | number | boolean>;
};
export type MapImageLayerQueryRelatedFeaturesOptions = Omit<MapRelatedRecordsRequest, "serviceId">;
export interface MapImageLayerQueryExtentResponse {
  extent: Record<string, unknown> | null;
  count?: number;
}
export type MapImageSublayerQueryOptions = Omit<MapImageLayerQueryOptions, "layerId">;
export type MapImageSublayerQueryAllOptions = Omit<MapImageLayerQueryAllOptions, "layerId">;
export type MapImageSublayerCreateQueryResult = MapImageSublayerQueryOptions;
export type MapImageSublayerQueryCountOptions = Omit<MapImageLayerQueryCountOptions, "layerId">;
export type MapImageSublayerQueryObjectIdsOptions = Omit<MapImageLayerQueryObjectIdsOptions, "layerId">;
export type MapImageSublayerQueryExtentOptions = Omit<MapImageLayerQueryExtentOptions, "layerId">;
export type MapImageSublayerQueryRelatedFeaturesOptions = Omit<MapImageLayerQueryRelatedFeaturesOptions, "layerId">;
export type MapImageLayerSublayerLookupId = number | string;

export type MapImageLayerLoadStatusCompat = "not-loaded" | "loading" | "loaded" | "failed";

export interface MapImageLayerHandleCompat {
  remove(): void;
}

export interface MapImageSublayerCompatOptions {
  layer: MapImageLayerCompat;
  layerId: number;
  source?: Record<string, unknown>;
}

export class MapImageLayerCompat {
  public readonly url: string;
  public id: string;
  public title: string | undefined;
  public readonly serviceId: string;
  public opacity: number;
  public visible: boolean;
  public minScale: number;
  public maxScale: number;
  public listMode: string;
  public legendEnabled: boolean;
  public loaded: boolean;
  public loadStatus: MapImageLayerLoadStatusCompat;
  public metadata: Record<string, unknown> | undefined;
  public readonly eventBus: CompatEventBus;

  private readonly client: HonuaClient;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;
  private readonly eventListeners: Map<string, Set<(event: unknown) => void>>;
  private readonly sublayerWrappersById: Map<number, MapImageSublayerCompat>;
  private sublayerSources: Record<string, unknown>[];

  public constructor(options: MapImageLayerCompatOptions) {
    const parsed = parseMapServiceUrl(options.url);
    this.url = options.url;
    this.serviceId = parsed.serviceId;
    this.id = options.id ?? this.serviceId;
    this.title = options.title;
    this.sublayerSources = Array.isArray(options.sublayers)
      ? [...options.sublayers]
      : ([] as Record<string, unknown>[]);
    this.opacity = options.opacity ?? 1;
    this.visible = options.visible ?? true;
    this.minScale = normalizeScale(options.minScale);
    this.maxScale = normalizeScale(options.maxScale);
    this.listMode = options.listMode ?? "show";
    this.legendEnabled = options.legendEnabled ?? true;
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.metadata = undefined;
    this.eventBus =
      options.eventBus ?? resolveCompatEventBus(options.client, options.sublayers) ?? new CompatEventBus();
    this.client = options.client ?? new HonuaClient({ baseUrl: parsed.baseUrl });
    this.watchListeners = new Map();
    this.eventListeners = new Map();
    this.sublayerWrappersById = new Map();
  }

  public async load(): Promise<MapImageLayerCompat> {
    if (!this.loaded) {
      this.loadStatus = "loading";
      this.notifyWatchers("loadStatus", this.loadStatus);
      this.eventBus.emit("map-image-layer.loading", { serviceId: this.serviceId, id: this.id }, this);
      try {
        this.metadata = (await this.client.getMapServiceMetadata(this.serviceId)) as unknown as
          | Record<string, unknown>
          | undefined;
        this.notifyWatchers("metadata", this.metadata);
        this.hydrateSublayersFromMetadataIfNeeded();
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

  public watch(
    propertyName: "visible" | "loaded" | "legendEnabled",
    listener: (value: boolean) => void,
  ): MapImageLayerHandleCompat;
  public watch(
    propertyName: "opacity" | "minScale" | "maxScale",
    listener: (value: number) => void,
  ): MapImageLayerHandleCompat;
  public watch(
    propertyName: "loadStatus",
    listener: (value: MapImageLayerLoadStatusCompat) => void,
  ): MapImageLayerHandleCompat;
  public watch(propertyName: string, listener: (value: unknown) => void): MapImageLayerHandleCompat;
  public watch(propertyName: string, listener: (value: any) => void): MapImageLayerHandleCompat {
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

  public get sublayers(): readonly MapImageSublayerCompat[] {
    void this.synchronizeSublayerWrapperCache();

    const wrappers: MapImageSublayerCompat[] = [];
    const seenIds = new Set<number>();
    for (const source of this.sublayerSources) {
      const sourceId = extractSublayerId(source);
      if (sourceId === undefined || seenIds.has(sourceId)) {
        continue;
      }

      seenIds.add(sourceId);
      wrappers.push(this.getOrCreateSublayerWrapper(sourceId, source));
    }
    return wrappers;
  }

  public set sublayers(sublayers: readonly (Record<string, unknown> | MapImageSublayerCompat)[]) {
    this.setSublayers(sublayers);
  }

  public get allSublayers(): readonly MapImageSublayerCompat[] {
    return this.synchronizeSublayerWrapperCache();
  }

  public exportImage(options: MapImageLayerExportOptions): Promise<HonuaExportMapResponse> {
    return this.client.exportMap({
      ...options,
      serviceId: this.serviceId,
    });
  }

  public getLegend(options: MapImageLayerLegendOptions = {}): Promise<HonuaLegendResponse> {
    return this.client.getMapLegend({
      ...options,
      serviceId: this.serviceId,
    });
  }

  public legend(options: MapImageLayerLegendOptions = {}): Promise<HonuaLegendResponse> {
    return this.getLegend(options);
  }

  public identify(options: MapImageLayerIdentifyOptions): Promise<HonuaIdentifyResponse> {
    return this.client.identifyMap({
      ...options,
      serviceId: this.serviceId,
    });
  }

  public find(options: MapImageLayerFindOptions): Promise<HonuaFindResponse> {
    return this.client.findMap({
      ...options,
      serviceId: this.serviceId,
    });
  }

  public createQuery(layerId: number): MapImageLayerCreateQueryResult {
    return {
      layerId,
      where: "1=1",
      outFields: ["*"],
      returnGeometry: true,
    };
  }

  public queryFeatures(options: MapImageLayerQueryOptions): Promise<HonuaQueryResponse> {
    return this.client.queryMapLayer({
      ...options,
      serviceId: this.serviceId,
    });
  }

  public async queryFeaturesAll(options: MapImageLayerQueryAllOptions): Promise<unknown[]> {
    const pageSize =
      typeof options.pageSize === "number" && Number.isFinite(options.pageSize)
        ? Math.max(1, Math.trunc(options.pageSize))
        : 2000;
    const maxPages =
      typeof options.maxPages === "number" && Number.isFinite(options.maxPages)
        ? Math.max(1, Math.trunc(options.maxPages))
        : 100;

    const features: unknown[] = [];
    for (let page = 0; page < maxPages; page += 1) {
      const response = await this.queryFeatures({
        ...options,
        extraParams: {
          ...(options.extraParams ?? {}),
          resultOffset: page * pageSize,
          resultRecordCount: pageSize,
        },
      });

      const pageFeatures = extractFeatures(response);
      if (pageFeatures.length === 0) {
        break;
      }

      features.push(...pageFeatures);
      if (pageFeatures.length < pageSize) {
        break;
      }
    }

    return features;
  }

  public async *queryFeaturesStream(options: MapImageLayerQueryAllOptions): AsyncGenerator<unknown[], void, undefined> {
    const pageSize =
      typeof options.pageSize === "number" && Number.isFinite(options.pageSize)
        ? Math.max(1, Math.trunc(options.pageSize))
        : 2000;
    const maxPages =
      typeof options.maxPages === "number" && Number.isFinite(options.maxPages)
        ? Math.max(1, Math.trunc(options.maxPages))
        : 100;

    for (let page = 0; page < maxPages; page += 1) {
      const response = await this.queryFeatures({
        ...options,
        extraParams: {
          ...(options.extraParams ?? {}),
          resultOffset: page * pageSize,
          resultRecordCount: pageSize,
        },
      });

      const pageFeatures = extractFeatures(response);
      if (pageFeatures.length === 0) {
        break;
      }

      yield pageFeatures;
      if (pageFeatures.length < pageSize) {
        break;
      }
    }
  }

  public async queryFeatureCount(options: MapImageLayerQueryCountOptions): Promise<number> {
    const response = (await this.queryFeatures({
      layerId: options.layerId,
      where: options.where ?? "1=1",
      returnGeometry: false,
      outFields: "OBJECTID",
      method: options.method,
      extraParams: {
        returnCountOnly: true,
        ...options.extraParams,
      },
    })) as HonuaQueryResponse & { count?: number };
    return extractFeatureCount(response);
  }

  public async queryObjectIds(options: MapImageLayerQueryObjectIdsOptions): Promise<number[]> {
    const response = (await this.queryFeatures({
      layerId: options.layerId,
      where: options.where ?? "1=1",
      returnGeometry: false,
      outFields: "OBJECTID",
      method: options.method,
      extraParams: {
        returnIdsOnly: true,
        ...options.extraParams,
      },
    })) as HonuaQueryResponse & { objectIds?: unknown[] };
    return extractObjectIds(response);
  }

  public async queryExtent(options: MapImageLayerQueryExtentOptions): Promise<MapImageLayerQueryExtentResponse> {
    const response = (await this.queryFeatures({
      layerId: options.layerId,
      where: options.where ?? "1=1",
      returnGeometry: false,
      method: options.method,
      extraParams: {
        returnExtentOnly: true,
        ...options.extraParams,
      },
    })) as HonuaQueryResponse & { extent?: Record<string, unknown> | null; count?: number };
    return extractExtent(response);
  }

  public queryRelatedRecords(options: MapImageLayerQueryRelatedFeaturesOptions): Promise<HonuaRelatedRecordsResponse> {
    return this.client.queryMapRelatedRecords({
      ...options,
      serviceId: this.serviceId,
    });
  }

  public queryRelatedFeatures(options: MapImageLayerQueryRelatedFeaturesOptions): Promise<HonuaRelatedRecordsResponse> {
    return this.queryRelatedRecords(options);
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

  public setSublayers(sublayers: readonly (Record<string, unknown> | MapImageSublayerCompat)[]): void {
    this.sublayerSources = sublayers.map((candidate) =>
      candidate instanceof MapImageSublayerCompat ? candidate.source : candidate,
    ) as Record<string, unknown>[];
    const wrappers = this.sublayers;
    this.notifyWatchers("sublayers", wrappers);
    this.eventBus.emit("map-image-layer.sublayers-changed", { layerId: this.id }, this);
  }

  public findSublayerById(id: MapImageLayerSublayerLookupId): MapImageSublayerCompat | undefined {
    const expectedId = normalizeSublayerId(id);
    if (expectedId === undefined) {
      return undefined;
    }

    const source = findSublayerSourceById(this.sublayerSources, expectedId);
    if (source === undefined) {
      return undefined;
    }
    return this.getOrCreateSublayerWrapper(expectedId, source);
  }

  public sublayer(id: MapImageLayerSublayerLookupId): MapImageSublayerCompat | undefined {
    return this.findSublayerById(id);
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

  public on(eventName: string, listener: (event: unknown) => void): MapImageLayerHandleCompat {
    const namespacedEvent = `map-image-layer.${eventName}`;
    let listeners = this.eventListeners.get(eventName);
    if (!listeners) {
      listeners = new Set();
      this.eventListeners.set(eventName, listeners);
    }
    listeners.add(listener);

    const subscription = this.eventBus.on(namespacedEvent, (event) => {
      safeInvokeCompatListener(listener, event.payload);
    });

    return {
      remove: () => {
        listeners?.delete(listener);
        subscription.remove();
      },
    };
  }

  public destroy(): void {
    this.watchListeners.clear();
    this.eventListeners.clear();
    this.eventBus.emit("map-image-layer.destroyed", { id: this.id }, this);
  }

  public setLegendEnabled(legendEnabled: boolean): void {
    this.legendEnabled = legendEnabled;
    this.notifyWatchers("legendEnabled", this.legendEnabled);
    this.eventBus.emit("map-image-layer.legend-enabled-changed", { layerId: this.id, legendEnabled }, this);
  }

  public listFields(): readonly HonuaFieldInfo[] {
    return extractFieldDefinitions(this.metadata);
  }

  public getField(fieldName: string): HonuaFieldInfo | undefined {
    const normalizedFieldName = fieldName.trim();
    if (normalizedFieldName.length === 0) {
      return undefined;
    }

    return this.listFields().find((field) => {
      return field.name.trim() === normalizedFieldName;
    });
  }

  private hydrateSublayersFromMetadataIfNeeded(): void {
    if (this.sublayerSources.length > 0) {
      return;
    }

    const metadataSublayers = extractSublayersFromMetadata(this.metadata);
    if (metadataSublayers.length === 0) {
      return;
    }

    this.sublayerSources = metadataSublayers;
    const wrappers = this.sublayers;
    this.notifyWatchers("sublayers", wrappers);
    this.eventBus.emit("map-image-layer.sublayers-changed", { layerId: this.id, source: "metadata" }, this);
  }

  public getOrCreateSublayerWrapper(layerId: number, source: Record<string, unknown>): MapImageSublayerCompat {
    const existing = this.sublayerWrappersById.get(layerId);
    if (existing) {
      existing.setSource(source);
      return existing;
    }

    const created = new MapImageSublayerCompat({
      layer: this,
      layerId,
      source,
    });
    this.sublayerWrappersById.set(layerId, created);
    return created;
  }

  private synchronizeSublayerWrapperCache(): MapImageSublayerCompat[] {
    const currentSublayerIds = new Set<number>();
    const wrappers: MapImageSublayerCompat[] = [];
    collectSublayerWrappers(this.sublayerSources, this, wrappers, currentSublayerIds);

    for (const cachedLayerId of this.sublayerWrappersById.keys()) {
      if (!currentSublayerIds.has(cachedLayerId)) {
        this.sublayerWrappersById.delete(cachedLayerId);
      }
    }

    return wrappers;
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

export class MapImageSublayerCompat {
  public readonly layer: MapImageLayerCompat;
  public readonly layerId: number;
  public source: Record<string, unknown>;

  public constructor(options: MapImageSublayerCompatOptions) {
    this.layer = options.layer;
    this.layerId = options.layerId;
    this.source = options.source ?? { id: options.layerId };
  }

  public setSource(source: Record<string, unknown>): void {
    this.source = source;
  }

  public get id(): number {
    return this.layerId;
  }

  public get visible(): boolean {
    if (typeof this.source.visible !== "boolean") {
      return true;
    }
    return this.source.visible;
  }

  public set visible(visible: boolean) {
    this.source.visible = visible;
  }

  public get title(): string | undefined {
    if (typeof this.source.title !== "string") {
      return undefined;
    }
    return this.source.title;
  }

  public get definitionExpression(): string | undefined {
    if (typeof this.source.definitionExpression !== "string") {
      return undefined;
    }
    return this.source.definitionExpression;
  }

  public set definitionExpression(definitionExpression: string | undefined) {
    if (
      definitionExpression === undefined ||
      (typeof definitionExpression === "string" && definitionExpression.trim().length === 0)
    ) {
      delete this.source.definitionExpression;
      return;
    }
    this.source.definitionExpression = definitionExpression;
  }

  public setVisibility(visible: boolean): void {
    this.visible = visible;
    this.layer.eventBus.emit("layer.visibility-changed", { layerId: this.layerId, visible: this.visible }, this);
  }

  public createQuery(): MapImageSublayerCreateQueryResult {
    const query = this.layer.createQuery(this.layerId);
    return {
      where: query.where,
      outFields: query.outFields,
      returnGeometry: query.returnGeometry,
      method: query.method,
      extraParams: query.extraParams,
    };
  }

  public queryFeatures(options: MapImageSublayerQueryOptions = {}): Promise<HonuaQueryResponse> {
    return this.layer.queryFeatures({
      layerId: this.layerId,
      where: options.where ?? this.definitionExpression ?? "1=1",
      ...options,
    });
  }

  public queryFeaturesAll(options: MapImageSublayerQueryAllOptions = {}): Promise<unknown[]> {
    return this.layer.queryFeaturesAll({
      layerId: this.layerId,
      where: options.where ?? this.definitionExpression ?? "1=1",
      ...options,
    });
  }

  public queryFeatureCount(options: MapImageSublayerQueryCountOptions = {}): Promise<number> {
    return this.layer.queryFeatureCount({
      layerId: this.layerId,
      where: options.where ?? this.definitionExpression,
      ...options,
    });
  }

  public queryObjectIds(options: MapImageSublayerQueryObjectIdsOptions = {}): Promise<number[]> {
    return this.layer.queryObjectIds({
      layerId: this.layerId,
      where: options.where ?? this.definitionExpression,
      ...options,
    });
  }

  public queryExtent(options: MapImageSublayerQueryExtentOptions = {}): Promise<MapImageLayerQueryExtentResponse> {
    return this.layer.queryExtent({
      layerId: this.layerId,
      where: options.where ?? this.definitionExpression,
      ...options,
    });
  }

  public queryRelatedRecords(
    options: MapImageSublayerQueryRelatedFeaturesOptions,
  ): Promise<HonuaRelatedRecordsResponse> {
    return this.layer.queryRelatedRecords({
      layerId: this.layerId,
      where: options.where ?? this.definitionExpression,
      ...options,
    });
  }

  public queryRelatedFeatures(
    options: MapImageSublayerQueryRelatedFeaturesOptions,
  ): Promise<HonuaRelatedRecordsResponse> {
    return this.queryRelatedRecords(options);
  }

  public get sublayers(): readonly MapImageSublayerCompat[] {
    const childSources = getChildSublayers(this.source);
    const wrappers: MapImageSublayerCompat[] = [];
    for (const childSource of childSources) {
      const childId = extractSublayerId(childSource);
      if (childId === undefined) {
        continue;
      }
      wrappers.push(this.layer.getOrCreateSublayerWrapper(childId, childSource));
    }
    return wrappers;
  }

  public get allSublayers(): readonly MapImageSublayerCompat[] {
    const wrappers: MapImageSublayerCompat[] = [];
    const trackedIds = new Set<number>();
    collectSublayerWrappers(getChildSublayers(this.source), this.layer, wrappers, trackedIds);
    return wrappers;
  }

  public findSublayerById(id: MapImageLayerSublayerLookupId): MapImageSublayerCompat | undefined {
    const expectedId = normalizeSublayerId(id);
    if (expectedId === undefined) {
      return undefined;
    }

    const source = findSublayerSourceById(getChildSublayers(this.source), expectedId);
    if (source === undefined) {
      return undefined;
    }
    return this.layer.getOrCreateSublayerWrapper(expectedId, source);
  }

  public sublayer(id: MapImageLayerSublayerLookupId): MapImageSublayerCompat | undefined {
    return this.findSublayerById(id);
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

function extractSublayerId(sublayer: Record<string, unknown>): number | undefined {
  const id = sublayer.id;
  if (id === undefined) {
    return undefined;
  }
  return normalizeSublayerId(id as MapImageLayerSublayerLookupId);
}

function getChildSublayers(source: Record<string, unknown>): Record<string, unknown>[] {
  if (Array.isArray(source.sublayers)) {
    return [...source.sublayers] as Record<string, unknown>[];
  }
  if (Array.isArray(source.allSublayers)) {
    return [...source.allSublayers] as Record<string, unknown>[];
  }
  return [];
}

function collectSublayerWrappers(
  sources: readonly Record<string, unknown>[],
  layer: MapImageLayerCompat,
  wrappers: MapImageSublayerCompat[],
  trackedIds: Set<number>,
): void {
  for (const source of sources) {
    const sourceId = extractSublayerId(source);
    if (sourceId === undefined || trackedIds.has(sourceId)) {
      continue;
    }

    trackedIds.add(sourceId);
    wrappers.push(layer.getOrCreateSublayerWrapper(sourceId, source));
    collectSublayerWrappers(getChildSublayers(source), layer, wrappers, trackedIds);
  }
}

function findSublayerSourceById(
  sources: readonly Record<string, unknown>[],
  expectedId: number,
): Record<string, unknown> | undefined {
  for (const source of sources) {
    if (extractSublayerId(source) === expectedId) {
      return source;
    }

    const nested = findSublayerSourceById(getChildSublayers(source), expectedId);
    if (nested !== undefined) {
      return nested;
    }
  }
  return undefined;
}

function extractSublayersFromMetadata(metadata: Record<string, unknown> | undefined): Record<string, unknown>[] {
  if (!isRecord(metadata) || !Array.isArray(metadata.layers)) {
    return [];
  }

  const sublayersById = new Map<number, Record<string, unknown>>();
  const orderedIds: number[] = [];
  const seenIds = new Set<number>();
  for (const entry of metadata.layers) {
    if (!isRecord(entry)) {
      continue;
    }

    const layerId = normalizeSublayerId(entry.id as MapImageLayerSublayerLookupId);
    if (layerId === undefined || seenIds.has(layerId)) {
      continue;
    }

    const hydrated = { ...entry, id: layerId } as Record<string, unknown>;
    if (typeof hydrated.title !== "string" && typeof hydrated.name === "string") {
      hydrated.title = hydrated.name;
    }
    hydrated.sublayers = [];
    sublayersById.set(layerId, hydrated);
    orderedIds.push(layerId);
    seenIds.add(layerId);
  }

  if (sublayersById.size === 0) {
    return [];
  }

  const roots: Record<string, unknown>[] = [];
  for (const layerId of orderedIds) {
    const hydrated = sublayersById.get(layerId);
    if (!hydrated) {
      continue;
    }

    const parentId = normalizeSublayerId(hydrated.parentLayerId as MapImageLayerSublayerLookupId);
    const parent = parentId === undefined ? undefined : sublayersById.get(parentId);
    if (parent && Array.isArray(parent.sublayers)) {
      parent.sublayers.push(hydrated);
    } else {
      roots.push(hydrated);
    }
  }

  return roots;
}

function extractFeatureCount(response: HonuaQueryResponse & { count?: number }): number {
  if (typeof response.count === "number" && Number.isFinite(response.count)) {
    return response.count;
  }
  if (Array.isArray(response.features)) {
    return response.features.length;
  }
  return 0;
}

function extractFeatures(response: HonuaQueryResponse): unknown[] {
  if (!Array.isArray(response.features)) {
    return [];
  }
  return response.features;
}

function extractObjectIds(response: HonuaQueryResponse & { objectIds?: unknown[] }): number[] {
  if (!Array.isArray(response.objectIds)) {
    return [];
  }
  return response.objectIds.map((value) => Number(value)).filter((value): value is number => Number.isFinite(value));
}

function extractExtent(
  response: HonuaQueryResponse & { extent?: Record<string, unknown> | null; count?: number },
): MapImageLayerQueryExtentResponse {
  const count = typeof response.count === "number" && Number.isFinite(response.count) ? response.count : undefined;
  return {
    extent: (response.extent ?? null) as Record<string, unknown> | null,
    count,
  };
}

function extractFieldDefinitions(metadata: Record<string, unknown> | undefined): HonuaFieldInfo[] {
  if (!isRecord(metadata)) {
    return [];
  }

  const fields = metadata.fields;
  if (!Array.isArray(fields)) {
    return [];
  }

  const records: HonuaFieldInfo[] = [];
  for (const field of fields) {
    if (!isRecord(field) || typeof field.name !== "string" || typeof field.type !== "string") {
      continue;
    }
    records.push(field as unknown as HonuaFieldInfo);
  }
  return records;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}
