export interface ExtentCompatOptions {
  xmin?: number;
  ymin?: number;
  xmax?: number;
  ymax?: number;
  zmin?: number;
  zmax?: number;
  mmin?: number;
  mmax?: number;
  spatialReference?: unknown;
}

export type ExtentLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface ExtentHandleCompat {
  remove(): void;
}

export class ExtentCompat {
  public loaded: boolean;
  public loadStatus: ExtentLoadStatusCompat;
  public xmin: number;
  public ymin: number;
  public xmax: number;
  public ymax: number;
  public zmin: number | undefined;
  public zmax: number | undefined;
  public mmin: number | undefined;
  public mmax: number | undefined;
  public spatialReference: unknown;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: ExtentCompatOptions = {}) {
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.xmin = finiteNumber(options.xmin, 0);
    this.ymin = finiteNumber(options.ymin, 0);
    this.xmax = finiteNumber(options.xmax, 0);
    this.ymax = finiteNumber(options.ymax, 0);
    this.zmin = finiteNumberOrUndefined(options.zmin);
    this.zmax = finiteNumberOrUndefined(options.zmax);
    this.mmin = finiteNumberOrUndefined(options.mmin);
    this.mmax = finiteNumberOrUndefined(options.mmax);
    this.spatialReference = options.spatialReference;
    this.watchListeners = new Map();
  }

  public async load(): Promise<ExtentCompat> {
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

  public async when(callback?: (extent: ExtentCompat) => void): Promise<ExtentCompat> {
    const extent = await this.load();
    if (callback) {
      callback(extent);
    }
    return extent;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): ExtentHandleCompat {
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

  public update(options: ExtentCompatOptions): void {
    if (options.xmin !== undefined) {
      this.xmin = finiteNumber(options.xmin, this.xmin);
      this.notifyWatchers("xmin", this.xmin);
    }
    if (options.ymin !== undefined) {
      this.ymin = finiteNumber(options.ymin, this.ymin);
      this.notifyWatchers("ymin", this.ymin);
    }
    if (options.xmax !== undefined) {
      this.xmax = finiteNumber(options.xmax, this.xmax);
      this.notifyWatchers("xmax", this.xmax);
    }
    if (options.ymax !== undefined) {
      this.ymax = finiteNumber(options.ymax, this.ymax);
      this.notifyWatchers("ymax", this.ymax);
    }
    if (options.zmin !== undefined) {
      this.zmin = finiteNumberOrUndefined(options.zmin);
      this.notifyWatchers("zmin", this.zmin);
    }
    if (options.zmax !== undefined) {
      this.zmax = finiteNumberOrUndefined(options.zmax);
      this.notifyWatchers("zmax", this.zmax);
    }
    if (options.mmin !== undefined) {
      this.mmin = finiteNumberOrUndefined(options.mmin);
      this.notifyWatchers("mmin", this.mmin);
    }
    if (options.mmax !== undefined) {
      this.mmax = finiteNumberOrUndefined(options.mmax);
      this.notifyWatchers("mmax", this.mmax);
    }
    if (options.spatialReference !== undefined) {
      this.spatialReference = options.spatialReference;
      this.notifyWatchers("spatialReference", this.spatialReference);
    }
    this.notifyWatchers("center", this.center);
  }

  public get center(): { x: number; y: number } {
    return {
      x: (this.xmin + this.xmax) / 2,
      y: (this.ymin + this.ymax) / 2,
    };
  }

  public clone(): ExtentCompat {
    return new ExtentCompat(this.toJSON());
  }

  public toJSON(): ExtentCompatOptions {
    return {
      xmin: this.xmin,
      ymin: this.ymin,
      xmax: this.xmax,
      ymax: this.ymax,
      zmin: this.zmin,
      zmax: this.zmax,
      mmin: this.mmin,
      mmax: this.mmax,
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
      listener(value);
    }
  }
}

function finiteNumber(value: unknown, fallback: number): number {
  return typeof value === "number" && Number.isFinite(value) ? value : fallback;
}

function finiteNumberOrUndefined(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}
