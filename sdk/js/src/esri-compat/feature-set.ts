export interface FeatureSetCompatOptions {
  features?: unknown[];
  fields?: unknown[];
  geometryType?: string;
  spatialReference?: unknown;
  objectIdFieldName?: string;
  displayFieldName?: string;
}

export type FeatureSetLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface FeatureSetHandleCompat {
  remove(): void;
}

export class FeatureSetCompat {
  public loaded: boolean;
  public loadStatus: FeatureSetLoadStatusCompat;
  public features: unknown[];
  public fields: unknown[];
  public geometryType: string | undefined;
  public spatialReference: unknown;
  public objectIdFieldName: string | undefined;
  public displayFieldName: string | undefined;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: FeatureSetCompatOptions = {}) {
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.features = options.features ? [...options.features] : [];
    this.fields = options.fields ? [...options.fields] : [];
    this.geometryType = options.geometryType;
    this.spatialReference = options.spatialReference;
    this.objectIdFieldName = options.objectIdFieldName;
    this.displayFieldName = options.displayFieldName;
    this.watchListeners = new Map();
  }

  public async load(): Promise<FeatureSetCompat> {
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

  public async when(callback?: (featureSet: FeatureSetCompat) => void): Promise<FeatureSetCompat> {
    const featureSet = await this.load();
    if (callback) {
      callback(featureSet);
    }
    return featureSet;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): FeatureSetHandleCompat {
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

  public clone(): FeatureSetCompat {
    return new FeatureSetCompat(this.toJSON());
  }

  public toJSON(): FeatureSetCompatOptions {
    return {
      features: [...this.features],
      fields: [...this.fields],
      geometryType: this.geometryType,
      spatialReference: this.spatialReference,
      objectIdFieldName: this.objectIdFieldName,
      displayFieldName: this.displayFieldName,
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
