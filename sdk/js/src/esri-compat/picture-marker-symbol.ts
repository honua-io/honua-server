import { safeInvokeCompatListener } from "./event-bus.js";
export interface PictureMarkerSymbolCompatOptions {
  url?: string;
  width?: number | string;
  height?: number | string;
  xoffset?: number;
  yoffset?: number;
  angle?: number;
  opacity?: number;
}

export type PictureMarkerSymbolLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface PictureMarkerSymbolHandleCompat {
  remove(): void;
}

export class PictureMarkerSymbolCompat {
  public loaded: boolean;
  public loadStatus: PictureMarkerSymbolLoadStatusCompat;
  public url: string | undefined;
  public width: number | string | undefined;
  public height: number | string | undefined;
  public xoffset: number;
  public yoffset: number;
  public angle: number;
  public opacity: number;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: PictureMarkerSymbolCompatOptions = {}) {
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.url = options.url;
    this.width = options.width;
    this.height = options.height;
    this.xoffset = finiteNumber(options.xoffset, 0);
    this.yoffset = finiteNumber(options.yoffset, 0);
    this.angle = finiteNumber(options.angle, 0);
    this.opacity = clampOpacity(options.opacity);
    this.watchListeners = new Map();
  }

  public async load(): Promise<PictureMarkerSymbolCompat> {
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

  public async when(callback?: (symbol: PictureMarkerSymbolCompat) => void): Promise<PictureMarkerSymbolCompat> {
    const symbol = await this.load();
    if (callback) {
      callback(symbol);
    }
    return symbol;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): PictureMarkerSymbolHandleCompat {
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

  public update(options: PictureMarkerSymbolCompatOptions): void {
    if (options.url !== undefined) {
      this.url = options.url;
      this.notifyWatchers("url", this.url);
    }
    if (options.width !== undefined) {
      this.width = options.width;
      this.notifyWatchers("width", this.width);
    }
    if (options.height !== undefined) {
      this.height = options.height;
      this.notifyWatchers("height", this.height);
    }
    if (options.xoffset !== undefined) {
      this.xoffset = finiteNumber(options.xoffset, this.xoffset);
      this.notifyWatchers("xoffset", this.xoffset);
    }
    if (options.yoffset !== undefined) {
      this.yoffset = finiteNumber(options.yoffset, this.yoffset);
      this.notifyWatchers("yoffset", this.yoffset);
    }
    if (options.angle !== undefined) {
      this.angle = finiteNumber(options.angle, this.angle);
      this.notifyWatchers("angle", this.angle);
    }
    if (options.opacity !== undefined) {
      this.opacity = clampOpacity(options.opacity);
      this.notifyWatchers("opacity", this.opacity);
    }
  }

  public clone(): PictureMarkerSymbolCompat {
    return new PictureMarkerSymbolCompat(this.toJSON());
  }

  public toJSON(): PictureMarkerSymbolCompatOptions {
    return {
      url: this.url,
      width: this.width,
      height: this.height,
      xoffset: this.xoffset,
      yoffset: this.yoffset,
      angle: this.angle,
      opacity: this.opacity,
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

function finiteNumber(value: unknown, fallback: number): number {
  return typeof value === "number" && Number.isFinite(value) ? value : fallback;
}

function clampOpacity(value: unknown): number {
  const numeric = finiteNumber(value, 1);
  if (numeric < 0) {
    return 0;
  }
  if (numeric > 1) {
    return 1;
  }
  return numeric;
}