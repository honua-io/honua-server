import { safeInvokeCompatListener } from "./event-bus.js";
export interface GraphicCompatOptions {
  geometry?: unknown;
  symbol?: unknown;
  attributes?: Record<string, unknown>;
  popupTemplate?: unknown;
  layer?: unknown;
}

export type GraphicLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface GraphicHandleCompat {
  remove(): void;
}

export class GraphicCompat {
  public loaded: boolean;
  public loadStatus: GraphicLoadStatusCompat;
  public geometry: unknown;
  public symbol: unknown;
  public attributes: Record<string, unknown>;
  public popupTemplate: unknown;
  public layer: unknown;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: GraphicCompatOptions = {}) {
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.geometry = options.geometry;
    this.symbol = options.symbol;
    this.attributes = options.attributes ? { ...options.attributes } : {};
    this.popupTemplate = options.popupTemplate;
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

  public watch(propertyName: string, listener: (value: unknown) => void): GraphicHandleCompat {
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

  public setGeometry(geometry: unknown): void {
    this.geometry = geometry;
    this.notifyWatchers("geometry", this.geometry);
  }

  public setSymbol(symbol: unknown): void {
    this.symbol = symbol;
    this.notifyWatchers("symbol", this.symbol);
  }

  public setAttributes(attributes: Record<string, unknown>): void {
    this.attributes = { ...attributes };
    this.notifyWatchers("attributes", this.attributes);
  }

  public setPopupTemplate(popupTemplate: unknown): void {
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