import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";

export interface MapViewCompatOptions {
  map?: unknown;
  container?: unknown;
  center?: unknown;
  zoom?: number;
  eventBus?: CompatEventBus;
}

export interface MapViewGoToTarget {
  center?: unknown;
  zoom?: number;
}

export interface MapViewHandle {
  remove(): void;
}

export interface MapViewPopupOpenOptions {
  location?: unknown;
  features?: readonly unknown[];
  title?: string;
  content?: unknown;
}

export interface MapViewScreenPoint {
  x: number;
  y: number;
}

export interface MapViewMapPoint extends MapViewScreenPoint {
  spatialReference?: unknown;
}

export interface MapViewHitTestEvent {
  x?: number;
  y?: number;
  mapPoint?: MapViewMapPoint;
}

export interface MapViewHitTestResultItem {
  type: "graphic";
  graphic: unknown;
  layer?: unknown;
  mapPoint?: MapViewMapPoint;
}

export interface MapViewHitTestResult {
  results: MapViewHitTestResultItem[];
}

type PopupChangeType = "open" | "close";

export class MapViewPopupCompat {
  public visible: boolean;
  public location: unknown;
  public features: unknown[];
  public title: string | undefined;
  public content: unknown;

  private readonly onChange: (type: PopupChangeType, options?: MapViewPopupOpenOptions) => void;

  public constructor(onChange: (type: PopupChangeType, options?: MapViewPopupOpenOptions) => void) {
    this.visible = false;
    this.location = undefined;
    this.features = [];
    this.title = undefined;
    this.content = undefined;
    this.onChange = onChange;
  }

  public open(options: MapViewPopupOpenOptions = {}): void {
    this.visible = true;
    this.location = options.location;
    this.features = options.features ? [...options.features] : [];
    this.title = options.title;
    this.content = options.content;
    this.onChange("open", options);
  }

  public close(): void {
    if (!this.visible) {
      return;
    }

    this.visible = false;
    this.location = undefined;
    this.features = [];
    this.title = undefined;
    this.content = undefined;
    this.onChange("close");
  }
}

export class MapViewLayerViewCompat {
  public readonly layer: unknown;
  public updating: boolean;
  public suspended: boolean;

  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(layer: unknown) {
    this.layer = layer;
    this.updating = false;
    this.suspended = false;
    this.watchListeners = new Map();
  }

  public watch(propertyName: string, listener: (value: unknown) => void): MapViewHandle {
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

  public setUpdating(value: boolean): void {
    this.updating = value;
    this.notifyWatchers("updating", value);
  }

  public setSuspended(value: boolean): void {
    this.suspended = value;
    this.notifyWatchers("suspended", value);
  }

  public async queryFeatures(options: unknown = {}): Promise<unknown> {
    if (isQueryFeaturesProvider(this.layer)) {
      return this.layer.queryFeatures(options);
    }

    return { features: [] };
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

interface QueryFeaturesProvider {
  queryFeatures(options?: unknown): Promise<unknown> | unknown;
}

export class MapViewCompat {
  public map: unknown;
  public container: unknown;
  public center: unknown;
  public zoom: number | undefined;
  public readonly eventBus: CompatEventBus;
  public readonly popup: MapViewPopupCompat;

  private readonly eventListeners: Map<string, Set<(event: unknown) => void>>;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;
  private readonly layerViews: Map<unknown, MapViewLayerViewCompat>;
  private readonly readyPromise: Promise<MapViewCompat>;

  public constructor(options: MapViewCompatOptions = {}) {
    this.map = options.map;
    this.container = options.container;
    this.center = options.center;
    this.zoom = options.zoom;
    this.eventBus =
      options.eventBus ?? resolveCompatEventBus(options.map, options.container) ?? new CompatEventBus();
    this.popup = new MapViewPopupCompat((type, popupOptions) => {
      this.notifyWatchers("popup.visible", this.popup.visible);
      this.notifyWatchers("popup.features", this.popup.features);
      this.notifyWatchers("popup.location", this.popup.location);
      this.notifyWatchers("popup.title", this.popup.title);
      this.notifyWatchers("popup.content", this.popup.content);
      this.eventBus.emit(type === "open" ? "popup.open" : "popup.close", popupOptions, this);
      this.emit(type === "open" ? "popup-open" : "popup-close", popupOptions);
    });
    this.eventListeners = new Map();
    this.watchListeners = new Map();
    this.layerViews = new Map();
    this.readyPromise = Promise.resolve(this);
  }

  public async when(callback?: (view: MapViewCompat) => void): Promise<MapViewCompat> {
    const view = await this.readyPromise;
    if (callback) {
      callback(view);
    }

    return view;
  }

  public toMap(screenPoint: MapViewScreenPoint): MapViewMapPoint {
    return {
      x: screenPoint.x,
      y: screenPoint.y,
    };
  }

  public toScreen(mapPoint: MapViewMapPoint): MapViewScreenPoint {
    return {
      x: mapPoint.x,
      y: mapPoint.y,
    };
  }

  public async hitTest(event: MapViewHitTestEvent = {}): Promise<MapViewHitTestResult> {
    const mapPoint =
      event.mapPoint ??
      (typeof event.x === "number" && typeof event.y === "number"
        ? this.toMap({ x: event.x, y: event.y })
        : undefined);

    return {
      results: this.popup.features.map((feature) => ({
        type: "graphic",
        graphic: feature,
        layer: extractGraphicLayer(feature),
        mapPoint,
      })),
    };
  }

  public async goTo(target: MapViewGoToTarget): Promise<MapViewCompat> {
    if (target.center !== undefined) {
      this.center = target.center;
      this.notifyWatchers("center", this.center);
    }
    if (target.zoom !== undefined) {
      this.zoom = target.zoom;
      this.notifyWatchers("zoom", this.zoom);
    }
    this.eventBus.emit("view.go-to", target, this);
    this.emit("go-to", target);

    return this;
  }

  public openPopup(options: MapViewPopupOpenOptions = {}): void {
    this.popup.open(options);
  }

  public closePopup(): void {
    this.popup.close();
  }

  public async whenLayerView(layer: unknown): Promise<MapViewLayerViewCompat> {
    const existing = this.layerViews.get(layer);
    if (existing) {
      return existing;
    }

    const layerView = new MapViewLayerViewCompat(layer);
    this.layerViews.set(layer, layerView);
    this.eventBus.emit("view.layer-view-created", { layer, layerView }, this);
    this.emit("layerview-create", { layer, layerView });
    return layerView;
  }

  public on(eventName: string, listener: (event: unknown) => void): MapViewHandle {
    let listeners = this.eventListeners.get(eventName);
    if (!listeners) {
      listeners = new Set();
      this.eventListeners.set(eventName, listeners);
    }
    listeners.add(listener);

    return {
      remove: () => {
        listeners?.delete(listener);
      },
    };
  }

  public watch(propertyName: string, listener: (value: unknown) => void): MapViewHandle {
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

  public destroy(): void {
    this.eventBus.emit("view.destroy", undefined, this);
    this.emit("destroy", undefined);
    for (const layerView of this.layerViews.values()) {
      layerView.destroy();
    }
    this.layerViews.clear();
    this.closePopup();
    this.map = undefined;
    this.notifyWatchers("map", this.map);
    this.container = undefined;
    this.notifyWatchers("container", this.container);
    this.center = undefined;
    this.notifyWatchers("center", this.center);
    this.zoom = undefined;
    this.notifyWatchers("zoom", this.zoom);
    this.eventListeners.clear();
    this.watchListeners.clear();
  }

  private emit(eventName: string, payload: unknown): void {
    const listeners = this.eventListeners.get(eventName);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      listener(payload);
    }
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

function isQueryFeaturesProvider(value: unknown): value is QueryFeaturesProvider {
  return (
    typeof value === "object" &&
    value !== null &&
    "queryFeatures" in value &&
    typeof value.queryFeatures === "function"
  );
}

function extractGraphicLayer(value: unknown): unknown {
  if (typeof value !== "object" || value === null) {
    return undefined;
  }
  if (!("layer" in value)) {
    return undefined;
  }
  return value.layer;
}
