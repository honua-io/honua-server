export interface SpatialReferenceCompatOptions {
  wkid?: number;
  latestWkid?: number;
  wkt?: string;
  vcsWkid?: number;
  latestVcsWkid?: number;
}

export type SpatialReferenceLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface SpatialReferenceHandleCompat {
  remove(): void;
}

export class SpatialReferenceCompat {
  public loaded: boolean;
  public loadStatus: SpatialReferenceLoadStatusCompat;
  public wkid: number | undefined;
  public latestWkid: number | undefined;
  public wkt: string | undefined;
  public vcsWkid: number | undefined;
  public latestVcsWkid: number | undefined;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: SpatialReferenceCompatOptions = {}) {
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.wkid = finiteNumberOrUndefined(options.wkid);
    this.latestWkid = finiteNumberOrUndefined(options.latestWkid);
    this.wkt = options.wkt;
    this.vcsWkid = finiteNumberOrUndefined(options.vcsWkid);
    this.latestVcsWkid = finiteNumberOrUndefined(options.latestVcsWkid);
    this.watchListeners = new Map();
  }

  public async load(): Promise<SpatialReferenceCompat> {
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

  public async when(callback?: (spatialReference: SpatialReferenceCompat) => void): Promise<SpatialReferenceCompat> {
    const spatialReference = await this.load();
    if (callback) {
      callback(spatialReference);
    }
    return spatialReference;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): SpatialReferenceHandleCompat {
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

  public update(options: SpatialReferenceCompatOptions): void {
    if (options.wkid !== undefined) {
      this.wkid = finiteNumberOrUndefined(options.wkid);
      this.notifyWatchers("wkid", this.wkid);
    }
    if (options.latestWkid !== undefined) {
      this.latestWkid = finiteNumberOrUndefined(options.latestWkid);
      this.notifyWatchers("latestWkid", this.latestWkid);
    }
    if (options.wkt !== undefined) {
      this.wkt = options.wkt;
      this.notifyWatchers("wkt", this.wkt);
    }
    if (options.vcsWkid !== undefined) {
      this.vcsWkid = finiteNumberOrUndefined(options.vcsWkid);
      this.notifyWatchers("vcsWkid", this.vcsWkid);
    }
    if (options.latestVcsWkid !== undefined) {
      this.latestVcsWkid = finiteNumberOrUndefined(options.latestVcsWkid);
      this.notifyWatchers("latestVcsWkid", this.latestVcsWkid);
    }
  }

  public clone(): SpatialReferenceCompat {
    return new SpatialReferenceCompat(this.toJSON());
  }

  public toJSON(): SpatialReferenceCompatOptions {
    return {
      wkid: this.wkid,
      latestWkid: this.latestWkid,
      wkt: this.wkt,
      vcsWkid: this.vcsWkid,
      latestVcsWkid: this.latestVcsWkid,
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

function finiteNumberOrUndefined(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}
