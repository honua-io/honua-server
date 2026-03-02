import { safeInvokeCompatListener } from "./event-bus.js";
export interface SimpleFillSymbolCompatOptions {
  style?: string;
  color?: unknown;
  outline?: unknown;
}

export type SimpleFillSymbolLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface SimpleFillSymbolHandleCompat {
  remove(): void;
}

export class SimpleFillSymbolCompat {
  public loaded: boolean;
  public loadStatus: SimpleFillSymbolLoadStatusCompat;
  public style: string;
  public color: unknown;
  public outline: unknown;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: SimpleFillSymbolCompatOptions = {}) {
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.style = options.style ?? "solid";
    this.color = options.color;
    this.outline = options.outline;
    this.watchListeners = new Map();
  }

  public async load(): Promise<SimpleFillSymbolCompat> {
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

  public async when(callback?: (symbol: SimpleFillSymbolCompat) => void): Promise<SimpleFillSymbolCompat> {
    const symbol = await this.load();
    if (callback) {
      callback(symbol);
    }
    return symbol;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): SimpleFillSymbolHandleCompat {
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

  public update(options: SimpleFillSymbolCompatOptions): void {
    if (options.style !== undefined) {
      this.style = options.style;
      this.notifyWatchers("style", this.style);
    }
    if (options.color !== undefined) {
      this.color = options.color;
      this.notifyWatchers("color", this.color);
    }
    if (options.outline !== undefined) {
      this.outline = options.outline;
      this.notifyWatchers("outline", this.outline);
    }
  }

  public clone(): SimpleFillSymbolCompat {
    return new SimpleFillSymbolCompat(this.toJSON());
  }

  public toJSON(): SimpleFillSymbolCompatOptions {
    return {
      style: this.style,
      color: this.color,
      outline: this.outline,
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
