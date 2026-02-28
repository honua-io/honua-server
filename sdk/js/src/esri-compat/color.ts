import { safeInvokeCompatListener } from "./event-bus.js";
export type ColorCompatInput = string | number[] | Record<string, unknown>;

export type ColorLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface ColorHandleCompat {
  remove(): void;
}

export class ColorCompat {
  public loaded: boolean;
  public loadStatus: ColorLoadStatusCompat;
  private value: ColorCompatInput;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(input: ColorCompatInput = [0, 0, 0, 1]) {
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.value = normalizeInput(input);
    this.watchListeners = new Map();
  }

  public async load(): Promise<ColorCompat> {
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

  public async when(callback?: (color: ColorCompat) => void): Promise<ColorCompat> {
    const color = await this.load();
    if (callback) {
      callback(color);
    }
    return color;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): ColorHandleCompat {
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

  public set(input: ColorCompatInput): void {
    this.value = normalizeInput(input);
    this.notifyWatchers("value", this.toJSON());
  }

  public clone(): ColorCompat {
    return new ColorCompat(this.toJSON());
  }

  public toJSON(): ColorCompatInput {
    if (Array.isArray(this.value)) {
      return [...this.value];
    }
    if (typeof this.value === "object") {
      return { ...this.value };
    }
    return this.value;
  }

  public toCss(includeAlpha = true): string {
    if (typeof this.value === "string") {
      return this.value;
    }

    if (Array.isArray(this.value)) {
      const [r = 0, g = 0, b = 0, a = 1] = this.value;
      if (includeAlpha) {
        return `rgba(${r}, ${g}, ${b}, ${a})`;
      }
      return `rgb(${r}, ${g}, ${b})`;
    }

    const record = this.value;
    const r = toFiniteNumber(record.r, 0);
    const g = toFiniteNumber(record.g, 0);
    const b = toFiniteNumber(record.b, 0);
    const a = toFiniteNumber(record.a, 1);
    if (includeAlpha) {
      return `rgba(${r}, ${g}, ${b}, ${a})`;
    }
    return `rgb(${r}, ${g}, ${b})`;
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

function normalizeInput(input: ColorCompatInput): ColorCompatInput {
  if (typeof input === "string") {
    return input;
  }
  if (Array.isArray(input)) {
    return input.filter((value) => typeof value === "number" && Number.isFinite(value));
  }
  return { ...input };
}

function toFiniteNumber(value: unknown, fallback: number): number {
  return typeof value === "number" && Number.isFinite(value) ? value : fallback;
}