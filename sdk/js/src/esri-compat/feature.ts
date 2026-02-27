import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";

export interface FeatureCompatOptions {
  view?: unknown;
  map?: unknown;
  container?: unknown;
  graphic?: unknown;
  title?: string;
  eventBus?: CompatEventBus;
}

export class FeatureCompat {
  public readonly view: unknown;
  public readonly map: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public graphic: unknown;
  public title: string | undefined;

  public constructor(options: FeatureCompatOptions = {}) {
    this.view = options.view;
    this.map = options.map;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view, options.map) ?? new CompatEventBus();
    this.graphic = options.graphic;
    this.title = options.title;
  }

  public setGraphic(graphic: unknown, title?: string): void {
    this.graphic = graphic;
    if (title !== undefined) {
      this.title = title;
    }
    this.eventBus.emit("feature-widget.updated", { graphic: this.graphic, title: this.title }, this);
  }

  public clear(): void {
    this.graphic = undefined;
    this.eventBus.emit("feature-widget.cleared", undefined, this);
  }
}
