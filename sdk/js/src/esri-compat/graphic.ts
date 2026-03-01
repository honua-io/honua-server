import { safeInvokeCompatListener } from "./event-bus.js";

/** Structural type for geometry-like objects (point, polyline, polygon, extent, etc.). */
export type CompatGeometryLike = Record<string, unknown>;

/** Structural type for symbol-like objects (marker, line, fill, text, etc.). */
export type CompatSymbolLike = Record<string, unknown>;

/** Structural type for popup template-like objects. */
export type CompatPopupTemplateLike = Record<string, unknown>;

export interface GraphicCompatOptions {
  geometry?: CompatGeometryLike | null;
  symbol?: CompatSymbolLike | null;
  attributes?: Record<string, unknown>;
  popupTemplate?: CompatPopupTemplateLike | null;
  layer?: unknown;
}

export type GraphicLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface GraphicHandleCompat {
  remove(): void;
}

export class GraphicCompat {
  public loaded: boolean;
  public loadStatus: GraphicLoadStatusCompat;
  public geometry: CompatGeometryLike | null;
  public symbol: CompatSymbolLike | null;
  public attributes: Record<string, unknown>;
  public popupTemplate: CompatPopupTemplateLike | null;
  public layer: unknown;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: GraphicCompatOptions = {}) {
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.geometry = options.geometry ?? null;
    this.symbol = options.symbol ?? null;
    this.attributes = options.attributes ? { ...options.attributes } : {};
    this.popupTemplate = options.popupTemplate ?? null;
    this.layer = options.layer;
    this.watchListeners = new Map();
  }

  public async load(): Promise<GraphicCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    return this;
  }

  public async when(callback?: (graphic: GraphicCompat) => void): Promise<GraphicCompat> {
    const graphic = await this.load();
    if (callback) {
      callback(graphic);
    }
    return graphic;
  }

  public watch(propertyName: "loaded", listener: (value: boolean) => void): GraphicHandleCompat;
  public watch(propertyName: "loadStatus", listener: (value: GraphicLoadStatusCompat) => void): GraphicHandleCompat;
  public watch(propertyName: "geometry", listener: (value: CompatGeometryLike | null) => void): GraphicHandleCompat;
  public watch(propertyName: "symbol", listener: (value: CompatSymbolLike | null) => void): GraphicHandleCompat;
  public watch(propertyName: "attributes", listener: (value: Record<string, unknown>) => void): GraphicHandleCompat;
  public watch(propertyName: string, listener: (value: unknown) => void): GraphicHandleCompat;
  public watch(propertyName: string, listener: (value: any) => void): GraphicHandleCompat {
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

  public setGeometry(geometry: CompatGeometryLike | null): void {
    this.geometry = geometry;
    this.notifyWatchers("geometry", this.geometry);
  }

  public setSymbol(symbol: CompatSymbolLike | null): void {
    this.symbol = symbol;
    this.notifyWatchers("symbol", this.symbol);
  }

  public setAttributes(attributes: Record<string, unknown>): void {
    this.attributes = { ...attributes };
    this.notifyWatchers("attributes", this.attributes);
  }

  public setPopupTemplate(popupTemplate: CompatPopupTemplateLike | null): void {
    this.popupTemplate = popupTemplate;
    this.notifyWatchers("popupTemplate", this.popupTemplate);
  }

  public setLayer(layer: unknown): void {
    this.layer = layer;
    this.notifyWatchers("layer", this.layer);
  }

  public clone(): GraphicCompat {
    return new GraphicCompat({
      geometry: this.geometry,
      symbol: this.symbol,
      attributes: this.attributes,
      popupTemplate: this.popupTemplate,
      layer: this.layer,
    });
  }

  public toJSON(): Record<string, unknown> {
    return {
      geometry: this.geometry,
      symbol: this.symbol,
      attributes: { ...this.attributes },
      popupTemplate: this.popupTemplate,
      layer: this.layer,
    };
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
      safeInvokeCompatListener(listener, value);
    }
  }
}
