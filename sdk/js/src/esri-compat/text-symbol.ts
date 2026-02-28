import { safeInvokeCompatListener } from "./event-bus.js";
export interface TextSymbolCompatOptions {
  text?: string;
  color?: unknown;
  haloColor?: unknown;
  haloSize?: number | string;
  font?: unknown;
  xoffset?: number;
  yoffset?: number;
  angle?: number;
}

export type TextSymbolLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface TextSymbolHandleCompat {
  remove(): void;
}

export class TextSymbolCompat {
  public loaded: boolean;
  public loadStatus: TextSymbolLoadStatusCompat;
  public text: string;
  public color: unknown;
  public haloColor: unknown;
  public haloSize: number | string | undefined;
  public font: unknown;
  public xoffset: number;
  public yoffset: number;
  public angle: number;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: TextSymbolCompatOptions = {}) {
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.text = options.text ?? "";
    this.color = options.color;
    this.haloColor = options.haloColor;
    this.haloSize = options.haloSize;
    this.font = options.font;
    this.xoffset = finiteNumber(options.xoffset, 0);
    this.yoffset = finiteNumber(options.yoffset, 0);
    this.angle = finiteNumber(options.angle, 0);
    this.watchListeners = new Map();
  }

  public async load(): Promise<TextSymbolCompat> {
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

  public async when(callback?: (symbol: TextSymbolCompat) => void): Promise<TextSymbolCompat> {
    const symbol = await this.load();
    if (callback) {
      callback(symbol);
    }
    return symbol;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): TextSymbolHandleCompat {
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

  public update(options: TextSymbolCompatOptions): void {
    if (options.text !== undefined) {
      this.text = options.text;
      this.notifyWatchers("text", this.text);
    }
    if (options.color !== undefined) {
      this.color = options.color;
      this.notifyWatchers("color", this.color);
    }
    if (options.haloColor !== undefined) {
      this.haloColor = options.haloColor;
      this.notifyWatchers("haloColor", this.haloColor);
    }
    if (options.haloSize !== undefined) {
      this.haloSize = options.haloSize;
      this.notifyWatchers("haloSize", this.haloSize);
    }
    if (options.font !== undefined) {
      this.font = options.font;
      this.notifyWatchers("font", this.font);
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
  }

  public clone(): TextSymbolCompat {
    return new TextSymbolCompat(this.toJSON());
  }

  public toJSON(): TextSymbolCompatOptions {
    return {
      text: this.text,
      color: this.color,
      haloColor: this.haloColor,
      haloSize: this.haloSize,
      font: this.font,
      xoffset: this.xoffset,
      yoffset: this.yoffset,
      angle: this.angle,
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