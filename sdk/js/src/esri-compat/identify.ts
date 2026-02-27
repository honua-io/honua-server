import type { QueryMethod } from "../core/types.js";
import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";

export interface IdentifyCompatOptions {
  view?: unknown;
  layers?: readonly unknown[];
  eventBus?: CompatEventBus;
  autoOpenPopup?: boolean;
  includeHidden?: boolean;
}

export type IdentifyLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface IdentifyHandleCompat {
  remove(): void;
}

export interface IdentifyCompatRequest {
  geometry: string | Record<string, unknown>;
  geometryType?: string;
  sr?: string | number;
  layers?: string;
  tolerance?: number;
  mapExtent?: string | [number, number, number, number];
  imageDisplay?: string | [number, number, number];
  returnGeometry?: boolean;
  responseFormat?: "json" | "pjson";
  maxAllowableOffset?: number;
  layerDefs?: string;
  dynamicLayers?: string;
  time?: string;
  method?: QueryMethod;
  extraParams?: Record<string, string | number | boolean>;
  layerSources?: readonly unknown[];
  popupTitle?: string;
  popupContent?: unknown;
  popupLocation?: unknown;
  openPopup?: boolean;
}

export interface IdentifyCompatLayerResult {
  layer: unknown;
  response: unknown;
  features: unknown[];
}

export interface IdentifyCompatLayerError {
  layer: unknown;
  error: unknown;
}

export interface IdentifyCompatResult {
  layers: IdentifyCompatLayerResult[];
  errors: IdentifyCompatLayerError[];
  features: unknown[];
  totalResultCount: number;
}

export class IdentifyCompat {
  public readonly view: unknown;
  public readonly eventBus: CompatEventBus;
  public readonly autoOpenPopup: boolean;
  public readonly includeHidden: boolean;
  public loaded: boolean;
  public loadStatus: IdentifyLoadStatusCompat;
  public lastResult: IdentifyCompatResult | undefined;

  private readonly explicitLayers: readonly unknown[] | undefined;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: IdentifyCompatOptions = {}) {
    this.view = options.view;
    this.explicitLayers = options.layers;
    this.eventBus =
      options.eventBus ?? resolveCompatEventBus(options.view, options.layers) ?? new CompatEventBus();
    this.autoOpenPopup = options.autoOpenPopup ?? true;
    this.includeHidden = options.includeHidden ?? false;
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.lastResult = undefined;
    this.watchListeners = new Map();
  }

  public async load(): Promise<IdentifyCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("identify.loading", undefined, this);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("identify.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (identify: IdentifyCompat) => void): Promise<IdentifyCompat> {
    const identify = await this.load();
    if (callback) {
      callback(identify);
    }
    return identify;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): IdentifyHandleCompat {
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

  public async identify(request: IdentifyCompatRequest): Promise<IdentifyCompatResult> {
    await this.load();

    const candidates = this.resolveCandidateLayers(request.layerSources);
    const identifyLayers = candidates.filter((layer): layer is IdentifyProvider => {
      if (!isIdentifyProvider(layer)) {
        return false;
      }
      if (this.includeHidden) {
        return true;
      }
      return !isLayerExplicitlyHidden(layer);
    });

    this.eventBus.emit(
      "identify.started",
      {
        candidateCount: candidates.length,
        identifyLayerCount: identifyLayers.length,
      },
      this,
    );

    const mapExtent = normalizeMapExtent(request.mapExtent, this.view);
    const imageDisplay = normalizeImageDisplay(request.imageDisplay, this.view);
    const layerResults: IdentifyCompatLayerResult[] = [];
    const errors: IdentifyCompatLayerError[] = [];
    const features: unknown[] = [];

    for (const layer of identifyLayers) {
      try {
        const response = await layer.identify({
          geometry: request.geometry,
          geometryType: request.geometryType,
          sr: request.sr,
          layers: request.layers,
          tolerance: request.tolerance,
          mapExtent,
          imageDisplay,
          returnGeometry: request.returnGeometry,
          responseFormat: request.responseFormat,
          maxAllowableOffset: request.maxAllowableOffset,
          layerDefs: request.layerDefs,
          dynamicLayers: request.dynamicLayers,
          time: request.time,
          method: request.method,
          extraParams: request.extraParams,
        });
        const layerFeatures = extractIdentifyFeatures(response);
        layerResults.push({
          layer,
          response,
          features: layerFeatures,
        });
        features.push(...layerFeatures);
        this.eventBus.emit(
          "identify.layer-completed",
          {
            layerId: extractLayerId(layer),
            layerTitle: extractLayerTitle(layer),
            resultCount: layerFeatures.length,
          },
          this,
        );
      } catch (error) {
        errors.push({ layer, error });
        this.eventBus.emit(
          "identify.layer-error",
          {
            layerId: extractLayerId(layer),
            layerTitle: extractLayerTitle(layer),
          },
          this,
        );
      }
    }

    const result: IdentifyCompatResult = {
      layers: layerResults,
      errors,
      features,
      totalResultCount: features.length,
    };
    this.lastResult = result;
    this.notifyWatchers("lastResult", this.lastResult);

    const shouldOpenPopup = request.openPopup ?? this.autoOpenPopup;
    if (shouldOpenPopup && features.length > 0) {
      const popupLocation =
        request.popupLocation ?? extractPopupLocation(request.geometry) ?? extractViewCenter(this.view);
      openViewPopup(this.view, {
        location: popupLocation,
        features,
        title: request.popupTitle ?? `Identify (${features.length})`,
        content: request.popupContent,
      });
      this.eventBus.emit(
        "identify.popup-opened",
        {
          resultCount: features.length,
        },
        this,
      );
    }

    this.eventBus.emit(
      "identify.completed",
      {
        resultCount: features.length,
        layerCount: layerResults.length,
        errorCount: errors.length,
      },
      this,
    );
    return result;
  }

  public identifyAt(
    point: string | Record<string, unknown>,
    request: Omit<IdentifyCompatRequest, "geometry"> = {},
  ): Promise<IdentifyCompatResult> {
    return this.identify({
      ...request,
      geometry: point,
    });
  }

  private resolveCandidateLayers(layerSources: readonly unknown[] | undefined): unknown[] {
    if (layerSources) {
      return [...layerSources];
    }
    if (this.explicitLayers) {
      return [...this.explicitLayers];
    }

    return resolveViewLayers(this.view);
  }

  public destroy(): void {
    this.watchListeners.clear();
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

interface IdentifyProvider {
  identify(options: IdentifyRequestForProvider): Promise<unknown> | unknown;
  visible?: boolean;
}

interface IdentifyRequestForProvider {
  geometry: string | Record<string, unknown>;
  geometryType?: string;
  sr?: string | number;
  layers?: string;
  tolerance?: number;
  mapExtent: string | [number, number, number, number];
  imageDisplay: string | [number, number, number];
  returnGeometry?: boolean;
  responseFormat?: "json" | "pjson";
  maxAllowableOffset?: number;
  layerDefs?: string;
  dynamicLayers?: string;
  time?: string;
  method?: QueryMethod;
  extraParams?: Record<string, string | number | boolean>;
}

interface PopupTargetLike {
  openPopup(options: PopupOpenOptionsCompat): void;
  popup?: {
    open(options?: PopupOpenOptionsCompat): void;
  };
}

interface PopupOpenOptionsCompat {
  location?: unknown;
  features?: readonly unknown[];
  title?: string;
  content?: unknown;
}

function resolveViewLayers(view: unknown): unknown[] {
  if (!isRecord(view) || !isRecord(view.map)) {
    return [];
  }

  const map = view.map as Record<string, unknown>;
  if (Array.isArray(map.allLayers)) {
    return [...map.allLayers];
  }
  if (Array.isArray(map.layers)) {
    return [...map.layers];
  }
  return [];
}

function openViewPopup(view: unknown, options: PopupOpenOptionsCompat): void {
  if (!isRecord(view)) {
    return;
  }

  const popupTarget = view as PopupTargetLike;
  if (typeof popupTarget.openPopup === "function") {
    popupTarget.openPopup(options);
    return;
  }

  if (popupTarget.popup && typeof popupTarget.popup.open === "function") {
    popupTarget.popup.open(options);
  }
}

function extractPopupLocation(geometry: string | Record<string, unknown>): unknown {
  if (typeof geometry === "string") {
    return undefined;
  }

  if ("x" in geometry && "y" in geometry) {
    return geometry;
  }
  return undefined;
}

function extractViewCenter(view: unknown): unknown {
  if (!isRecord(view)) {
    return undefined;
  }
  return view.center;
}

function normalizeMapExtent(
  mapExtent: string | [number, number, number, number] | undefined,
  view: unknown,
): string | [number, number, number, number] {
  if (mapExtent !== undefined) {
    return mapExtent;
  }
  const viewExtent = extractExtentFromView(view);
  return viewExtent ?? "0,0,0,0";
}

function normalizeImageDisplay(
  imageDisplay: string | [number, number, number] | undefined,
  view: unknown,
): string | [number, number, number] {
  if (imageDisplay !== undefined) {
    return imageDisplay;
  }
  const width = extractOptionalNumber(view, "width");
  const height = extractOptionalNumber(view, "height");
  const dpi = extractOptionalNumber(view, "dpi") ?? 96;
  if (width !== undefined && height !== undefined) {
    return [width, height, dpi];
  }
  return "400,400,96";
}

function extractExtentFromView(view: unknown): [number, number, number, number] | undefined {
  if (!isRecord(view) || !isRecord(view.extent)) {
    return undefined;
  }

  const xmin = toOptionalNumber(view.extent.xmin);
  const ymin = toOptionalNumber(view.extent.ymin);
  const xmax = toOptionalNumber(view.extent.xmax);
  const ymax = toOptionalNumber(view.extent.ymax);
  if (xmin === undefined || ymin === undefined || xmax === undefined || ymax === undefined) {
    return undefined;
  }
  return [xmin, ymin, xmax, ymax];
}

function extractIdentifyFeatures(response: unknown): unknown[] {
  if (!isRecord(response)) {
    return [];
  }
  if (!Array.isArray(response.results)) {
    return [];
  }

  const features: unknown[] = [];
  for (const result of response.results) {
    if (!isRecord(result)) {
      continue;
    }
    if ("feature" in result) {
      features.push(result.feature);
      continue;
    }
    features.push(result);
  }
  return features;
}

function isIdentifyProvider(value: unknown): value is IdentifyProvider {
  return isRecord(value) && typeof value.identify === "function";
}

function isLayerExplicitlyHidden(value: unknown): boolean {
  return isRecord(value) && value.visible === false;
}

function extractLayerId(layer: unknown): string | undefined {
  if (!isRecord(layer)) {
    return undefined;
  }
  return toOptionalString(layer.id);
}

function extractLayerTitle(layer: unknown): string | undefined {
  if (!isRecord(layer)) {
    return undefined;
  }
  return toOptionalString(layer.title);
}

function extractOptionalNumber(value: unknown, key: string): number | undefined {
  if (!isRecord(value)) {
    return undefined;
  }
  return toOptionalNumber(value[key]);
}

function toOptionalNumber(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function toOptionalString(value: unknown): string | undefined {
  return typeof value === "string" ? value : undefined;
}

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null;
}
