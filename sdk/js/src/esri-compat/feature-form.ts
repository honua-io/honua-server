import { CompatEventBus, resolveCompatEventBus, safeInvokeCompatListener } from "./event-bus.js";

export interface FeatureFormCompatOptions {
  view?: unknown;
  layer?: unknown;
  container?: unknown;
  feature?: unknown;
  fieldConfig?: readonly unknown[];
  groupDisplay?: string;
  headingLevel?: number;
  visibleElements?: unknown;
  eventBus?: CompatEventBus;
}

export interface FeatureFormSubmitResultCompat {
  valid: boolean;
  values: Readonly<Record<string, unknown>>;
  feature: unknown;
}

export type FeatureFormLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface FeatureFormHandleCompat {
  remove(): void;
}

export class FeatureFormCompat {
  public readonly view: unknown;
  public readonly layer: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public loaded: boolean;
  public loadStatus: FeatureFormLoadStatusCompat;
  public feature: unknown;
  public fieldConfig: readonly unknown[];
  public groupDisplay: string | undefined;
  public headingLevel: number | undefined;
  public visibleElements: unknown;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: FeatureFormCompatOptions = {}) {
    this.view = options.view;
    this.layer = options.layer;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view, options.layer) ?? new CompatEventBus();
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.feature = options.feature;
    this.fieldConfig = options.fieldConfig ? [...options.fieldConfig] : [];
    this.groupDisplay = options.groupDisplay;
    this.headingLevel = options.headingLevel;
    this.visibleElements = options.visibleElements;
    this.watchListeners = new Map();
  }

  public async load(): Promise<FeatureFormCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("feature-form.loading", undefined, this);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("feature-form.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (widget: FeatureFormCompat) => void): Promise<FeatureFormCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): FeatureFormHandleCompat {
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

  public setFeature(feature: unknown): void {
    this.feature = feature;
    this.notifyWatchers("feature", this.feature);
    this.eventBus.emit("feature-form.feature-changed", { feature }, this);
  }

  public async submit(values: Readonly<Record<string, unknown>> = {}): Promise<FeatureFormSubmitResultCompat> {
    const result: FeatureFormSubmitResultCompat = {
      valid: true,
      values: { ...values },
      feature: this.feature,
    };
    this.eventBus.emit("feature-form.submitted", result, this);
    return result;
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
