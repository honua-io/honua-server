export interface SimpleMarkerSymbolCompatOptions {
  style?: string;
  color?: unknown;
  size?: number;
  outline?: unknown;
}

export type SimpleMarkerSymbolLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface SimpleMarkerSymbolHandleCompat {
  remove(): void;
}

export class SimpleMarkerSymbolCompat {
  public loaded: boolean;
  public loadStatus: SimpleMarkerSymbolLoadStatusCompat;
  public style: string;
  public color: unknown;
  public size: number;
  public outline: unknown;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: SimpleMarkerSymbolCompatOptions = {}) {
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.style = options.style ?? "circle";
    this.color = options.color;
    this.size =
      typeof options.size === "number" && Number.isFinite(options.size)
        ? options.size
        : 8;
    this.outline = options.outline;
    this.watchListeners = new Map();
  }

  public async load(): Promise<SimpleMarkerSymbolCompat> {
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

  public async when(callback?: (symbol: SimpleMarkerSymbolCompat) => void): Promise<SimpleMarkerSymbolCompat> {
    const symbol = await this.load();
    if (callback) {
      callback(symbol);
    }
    return symbol;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): SimpleMarkerSymbolHandleCompat {
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

  public update(options: SimpleMarkerSymbolCompatOptions): void {
    if (options.style !== undefined) {
      this.style = options.style;
      this.notifyWatchers("style", this.style);
    }
    if (options.color !== undefined) {
      this.color = options.color;
      this.notifyWatchers("color", this.color);
    }
    if (options.size !== undefined) {
      this.size = typeof options.size === "number" && Number.isFinite(options.size) ? options.size : this.size;
      this.notifyWatchers("size", this.size);
    }
    if (options.outline !== undefined) {
      this.outline = options.outline;
      this.notifyWatchers("outline", this.outline);
    }
  }

  public clone(): SimpleMarkerSymbolCompat {
    return new SimpleMarkerSymbolCompat(this.toJSON());
  }

  public toJSON(): SimpleMarkerSymbolCompatOptions {
    return {
      style: this.style,
      color: this.color,
      size: this.size,
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
      listener(value);
    }
  }
}
