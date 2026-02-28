import { safeInvokeCompatListener } from "./event-bus.js";
export interface PointCompatOptions {
  x?: number;
  y?: number;
  z?: number;
  m?: number;
  spatialReference?: unknown;
}

export type PointLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface PointHandleCompat {
  remove(): void;
}

export class PointCompat {
  public loaded: boolean;
  public loadStatus: PointLoadStatusCompat;
  public x: number | undefined;
  public y: number | undefined;
  public z: number | undefined;
  public m: number | undefined;
  public spatialReference: unknown;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: PointCompatOptions = {}) {
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.x = normalizeFiniteNumber(options.x);
    this.y = normalizeFiniteNumber(options.y);
    this.z = normalizeFiniteNumber(options.z);
    this.m = normalizeFiniteNumber(options.m);
    this.spatialReference = options.spatialReference;
    this.watchListeners = new Map();
  }

  public async load(): Promise<PointCompat> {
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

  public async when(callback?: (point: PointCompat) => void): Promise<PointCompat> {
    const point = await this.load();
    if (callback) {
      callback(point);
    }
    return point;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): PointHandleCompat {
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

  public update(options: PointCompatOptions): void {
    if (options.x !== undefined) {
      this.x = normalizeFiniteNumber(options.x);
      this.notifyWatchers("x", this.x);
    }
    if (options.y !== undefined) {
      this.y = normalizeFiniteNumber(options.y);
      this.notifyWatchers("y", this.y);
    }
    if (options.z !== undefined) {
      this.z = normalizeFiniteNumber(options.z);
      this.notifyWatchers("z", this.z);
    }
    if (options.m !== undefined) {
      this.m = normalizeFiniteNumber(options.m);
      this.notifyWatchers("m", this.m);
    }
    if (options.spatialReference !== undefined) {
      this.spatialReference = options.spatialReference;
      this.notifyWatchers("spatialReference", this.spatialReference);
    }
  }

  public clone(): PointCompat {
    return new PointCompat(this.toJSON());
  }

  public toJSON(): PointCompatOptions {
    return {
      x: this.x,
      y: this.y,
      z: this.z,
      m: this.m,
      spatialReference: this.spatialReference,
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

function normalizeFiniteNumber(value: number | undefined): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}