import { CompatEventBus, resolveCompatEventBus, safeInvokeCompatListener } from "./event-bus.js";

export interface SwipeCompatOptions {
  view?: unknown;
  container?: unknown;
  leadingLayers?: readonly unknown[];
  trailingLayers?: readonly unknown[];
  position?: number;
  eventBus?: CompatEventBus;
}

export type SwipeLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface SwipeHandleCompat {
  remove(): void;
}

export class SwipeCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public loaded: boolean;
  public loadStatus: SwipeLoadStatusCompat;
  public leadingLayers: unknown[];
  public trailingLayers: unknown[];
  public position: number;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: SwipeCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.eventBus =
      options.eventBus ??
      resolveCompatEventBus(options.view, options.leadingLayers, options.trailingLayers) ??
      new CompatEventBus();
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.leadingLayers = options.leadingLayers ? [...options.leadingLayers] : [];
    this.trailingLayers = options.trailingLayers ? [...options.trailingLayers] : [];
    this.position = normalizePosition(options.position ?? 50);
    this.watchListeners = new Map();
  }

  public async load(): Promise<SwipeCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("swipe.loading", undefined, this);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("swipe.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (widget: SwipeCompat) => void): Promise<SwipeCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): SwipeHandleCompat {
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

  public setPosition(position: number): void {
    this.position = normalizePosition(position);
    this.notifyWatchers("position", this.position);
    this.eventBus.emit("swipe.position-changed", { position: this.position }, this);
  }

  public setLeadingLayers(layers: readonly unknown[]): void {
    this.leadingLayers = [...layers];
    this.notifyWatchers("leadingLayers", this.leadingLayers);
    this.eventBus.emit("swipe.layers-changed", { side: "leading", count: this.leadingLayers.length }, this);
  }

  public setTrailingLayers(layers: readonly unknown[]): void {
    this.trailingLayers = [...layers];
    this.notifyWatchers("trailingLayers", this.trailingLayers);
    this.eventBus.emit("swipe.layers-changed", { side: "trailing", count: this.trailingLayers.length }, this);
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

function normalizePosition(value: number): number {
  if (!Number.isFinite(value)) {
    return 50;
  }
  return Math.min(100, Math.max(0, Math.trunc(value)));
}
