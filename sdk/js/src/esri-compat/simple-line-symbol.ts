export interface SimpleLineSymbolCompatOptions {
  style?: string;
  color?: unknown;
  width?: number;
}

export type SimpleLineSymbolLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface SimpleLineSymbolHandleCompat {
  remove(): void;
}

export class SimpleLineSymbolCompat {
  public loaded: boolean;
  public loadStatus: SimpleLineSymbolLoadStatusCompat;
  public style: string;
  public color: unknown;
  public width: number;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: SimpleLineSymbolCompatOptions = {}) {
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.style = options.style ?? "solid";
    this.color = options.color;
    this.width =
      typeof options.width === "number" && Number.isFinite(options.width)
        ? options.width
        : 1;
    this.watchListeners = new Map();
  }

  public async load(): Promise<SimpleLineSymbolCompat> {
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

  public async when(callback?: (symbol: SimpleLineSymbolCompat) => void): Promise<SimpleLineSymbolCompat> {
    const symbol = await this.load();
    if (callback) {
      callback(symbol);
    }
    return symbol;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): SimpleLineSymbolHandleCompat {
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

  public update(options: SimpleLineSymbolCompatOptions): void {
    if (options.style !== undefined) {
      this.style = options.style;
      this.notifyWatchers("style", this.style);
    }
    if (options.color !== undefined) {
      this.color = options.color;
      this.notifyWatchers("color", this.color);
    }
    if (options.width !== undefined) {
      this.width = typeof options.width === "number" && Number.isFinite(options.width) ? options.width : this.width;
      this.notifyWatchers("width", this.width);
    }
  }

  public clone(): SimpleLineSymbolCompat {
    return new SimpleLineSymbolCompat(this.toJSON());
  }

  public toJSON(): SimpleLineSymbolCompatOptions {
    return {
      style: this.style,
      color: this.color,
      width: this.width,
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
