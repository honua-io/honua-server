import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";

export interface FeatureFormCompatOptions {
  view?: unknown;
  layer?: unknown;
  container?: unknown;
  feature?: unknown;
  fieldConfig?: readonly unknown[];
  eventBus?: CompatEventBus;
}

export interface FeatureFormSubmitResultCompat {
  valid: boolean;
  values: Readonly<Record<string, unknown>>;
  feature: unknown;
}

export class FeatureFormCompat {
  public readonly view: unknown;
  public readonly layer: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public feature: unknown;
  public fieldConfig: readonly unknown[];

  public constructor(options: FeatureFormCompatOptions = {}) {
    this.view = options.view;
    this.layer = options.layer;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view, options.layer) ?? new CompatEventBus();
    this.feature = options.feature;
    this.fieldConfig = options.fieldConfig ? [...options.fieldConfig] : [];
  }

  public setFeature(feature: unknown): void {
    this.feature = feature;
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
}
