export interface PolygonCompatOptions {
  rings?: unknown[][][];
  spatialReference?: unknown;
  hasZ?: boolean;
  hasM?: boolean;
}

export type PolygonLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface PolygonHandleCompat {
  remove(): void;
}

export class PolygonCompat {
  public loaded: boolean;
  public loadStatus: PolygonLoadStatusCompat;
  public rings: unknown[][][];
  public spatialReference: unknown;
  public hasZ: boolean;
  public hasM: boolean;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: PolygonCompatOptions = {}) {
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.rings = options.rings ? options.rings.map(cloneRing) : [];
    this.spatialReference = options.spatialReference;
    this.hasZ = options.hasZ ?? false;
    this.hasM = options.hasM ?? false;
    this.watchListeners = new Map();
  }

  public async load(): Promise<PolygonCompat> {
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

  public async when(callback?: (polygon: PolygonCompat) => void): Promise<PolygonCompat> {
    const polygon = await this.load();
    if (callback) {
      callback(polygon);
    }
    return polygon;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): PolygonHandleCompat {
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

  public addRing(ring: unknown[][]): void {
    this.rings.push(cloneRing(ring));
    this.notifyWatchers("rings", this.rings);
  }

  public removeRing(index: number): boolean {
    if (!Number.isInteger(index) || index < 0 || index >= this.rings.length) {
      return false;
    }
    this.rings.splice(index, 1);
    this.notifyWatchers("rings", this.rings);
    return true;
  }

  public update(options: PolygonCompatOptions): void {
    if (options.rings !== undefined) {
      this.rings = options.rings.map(cloneRing);
      this.notifyWatchers("rings", this.rings);
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

  public clone(): PolygonCompat {
    return new PolygonCompat(this.toJSON());
  }

  public toJSON(): PolygonCompatOptions {
    return {
      rings: this.rings.map(cloneRing),
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
      listener(value);
    }
  }
}

function cloneRing(ring: readonly unknown[][]): unknown[][] {
  return ring.map((point) => [...point]);
}
