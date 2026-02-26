import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";

export interface BasemapLayerListCompatOptions {
  view?: unknown;
  map?: unknown;
  container?: unknown;
  eventBus?: CompatEventBus;
}

export class BasemapLayerListCompat {
  public readonly view: unknown;
  public readonly map: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;

  public constructor(options: BasemapLayerListCompatOptions = {}) {
    this.view = options.view;
    this.map = options.map;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view, options.map) ?? new CompatEventBus();
  }

  public refresh(): void {
    this.eventBus.emit("basemap-layer-list.refreshed", undefined, this);
  }
}
