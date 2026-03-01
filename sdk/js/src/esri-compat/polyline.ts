import { safeInvokeCompatListener } from "./event-bus.js";
export interface PolylineCompatOptions {
  paths?: unknown[][][];
  spatialReference?: unknown;
  hasZ?: boolean;
  hasM?: boolean;
}

export type PolylineLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface PolylineHandleCompat {
  remove(): void;
}

export class PolylineCompat {
  public loaded: boolean;
  public loadStatus: PolylineLoadStatusCompat;
  public paths: unknown[][][];
  public spatialReference: unknown;
  public hasZ: boolean;
  public hasM: boolean;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: PolylineCompatOptions = {}) {
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.paths = options.paths ? options.paths.map(clonePath) : [];
    this.spatialReference = options.spatialReference;
    this.hasZ = options.hasZ ?? false;
    this.hasM = options.hasM ?? false;
    this.watchListeners = new Map();
  }

  public async load(): Promise<PolylineCompat> {
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

  public async when(callback?: (polyline: PolylineCompat) => void): Promise<PolylineCompat> {
    const polyline = await this.load();
    if (callback) {
      callback(polyline);
    }
    return polyline;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): PolylineHandleCompat {
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

  public addPath(path: unknown[][]): void {
    this.paths.push(clonePath(path));
    this.notifyWatchers("paths", this.paths);
  }

  public removePath(index: number): boolean {
    if (!Number.isInteger(index) || index < 0 || index >= this.paths.length) {
      return false;
    }
    this.paths.splice(index, 1);
    this.notifyWatchers("paths", this.paths);
    return true;
  }

  public update(options: PolylineCompatOptions): void {
    if (options.paths !== undefined) {
      this.paths = options.paths.map(clonePath);
      this.notifyWatchers("paths", this.paths);
    }
    if (options.spatialReference !== undefined) {
      this.spatialReference = options.spatialReference;
      this.notifyWatchers("spatialReference", this.spatialReference);
    }
    if (options.hasZ !== undefined) {
      this.hasZ = options.hasZ;
      this.notifyWatchers("hasZ", this.hasZ);
    }
    if (options.hasM !== undefined) {
      this.hasM = options.hasM;
      this.notifyWatchers("hasM", this.hasM);
    }
  }

  public clone(): PolylineCompat {
    return new PolylineCompat(this.toJSON());
  }

  public toJSON(): PolylineCompatOptions {
    return {
      paths: this.paths.map(clonePath),
      spatialReference: this.spatialReference,
      hasZ: this.hasZ,
      hasM: this.hasM,
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

function clonePath(path: readonly unknown[][]): unknown[][] {
  return path.map((point) => [...point]);
}
