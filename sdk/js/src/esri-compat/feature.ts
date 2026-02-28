import { CompatEventBus, resolveCompatEventBus, safeInvokeCompatListener } from "./event-bus.js";

export interface FeatureCompatOptions {
  view?: unknown;
  map?: unknown;
  container?: unknown;
  graphic?: unknown;
  title?: string;
  eventBus?: CompatEventBus;
}

export type FeatureLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface FeatureHandleCompat {
  remove(): void;
}

export class FeatureCompat {
  public readonly view: unknown;
  public readonly map: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public loaded: boolean;
  public loadStatus: FeatureLoadStatusCompat;
  public graphic: unknown;
  public title: string | undefined;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: FeatureCompatOptions = {}) {
    this.view = options.view;
    this.map = options.map;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view, options.map) ?? new CompatEventBus();
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.graphic = options.graphic;
    this.title = options.title;
    this.watchListeners = new Map();
  }

  public async load(): Promise<FeatureCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("feature-widget.loading", undefined, this);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("feature-widget.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (widget: FeatureCompat) => void): Promise<FeatureCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): FeatureHandleCompat {
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

  public setGraphic(graphic: unknown, title?: string): void {
    this.graphic = graphic;
    this.notifyWatchers("graphic", this.graphic);
    if (title !== undefined) {
      this.title = title;
      this.notifyWatchers("title", this.title);
    }
    this.eventBus.emit("feature-widget.updated", { graphic: this.graphic, title: this.title }, this);
  }

  public clear(): void {
    this.graphic = undefined;
    this.notifyWatchers("graphic", this.graphic);
    this.eventBus.emit("feature-widget.cleared", undefined, this);
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
