import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";

export interface BasemapCompatOptions {
  id?: string;
  title?: string;
  baseLayers?: readonly unknown[];
  referenceLayers?: readonly unknown[];
  eventBus?: CompatEventBus;
}

export class BasemapCompat {
  public readonly eventBus: CompatEventBus;
  public id: string | undefined;
  public title: string | undefined;
  public baseLayers: unknown[];
  public referenceLayers: unknown[];

  public constructor(options: BasemapCompatOptions = {}) {
    this.eventBus =
      options.eventBus ??
      resolveCompatEventBus(options.baseLayers, options.referenceLayers) ??
      new CompatEventBus();
    this.id = options.id;
    this.title = options.title ?? options.id;
    this.baseLayers = options.baseLayers ? [...options.baseLayers] : [];
    this.referenceLayers = options.referenceLayers ? [...options.referenceLayers] : [];
  }

  public static fromId(id: string): BasemapCompat {
    return new BasemapCompat({
      id,
      title: id,
    });
  }

  public setBaseLayers(layers: readonly unknown[]): void {
    this.baseLayers = [...layers];
    this.eventBus.emit("basemap.base-layers-changed", { count: this.baseLayers.length }, this);
  }

  public setReferenceLayers(layers: readonly unknown[]): void {
    this.referenceLayers = [...layers];
    this.eventBus.emit("basemap.reference-layers-changed", { count: this.referenceLayers.length }, this);
  }
}
