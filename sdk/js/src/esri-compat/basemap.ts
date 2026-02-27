import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";

export interface BasemapCompatOptions {
  id?: string;
  title?: string;
  baseLayers?: readonly unknown[];
  referenceLayers?: readonly unknown[];
  eventBus?: CompatEventBus;
}

export type BasemapLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export class BasemapCompat {
  public readonly eventBus: CompatEventBus;
  public id: string | undefined;
  public title: string | undefined;
  public baseLayers: unknown[];
  public referenceLayers: unknown[];
  public loaded: boolean;
  public loadStatus: BasemapLoadStatusCompat;

  public constructor(options: BasemapCompatOptions = {}) {
    this.eventBus =
      options.eventBus ??
      resolveCompatEventBus(options.baseLayers, options.referenceLayers) ??
      new CompatEventBus();
    this.id = options.id;
    this.title = options.title ?? options.id;
    this.baseLayers = options.baseLayers ? [...options.baseLayers] : [];
    this.referenceLayers = options.referenceLayers ? [...options.referenceLayers] : [];
    this.loaded = false;
    this.loadStatus = "not-loaded";
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

  public async load(): Promise<BasemapCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.eventBus.emit("basemap.loading", { id: this.id }, this);
    this.loaded = true;
    this.loadStatus = "loaded";
    this.eventBus.emit("basemap.loaded", { id: this.id }, this);
    return this;
  }

  public async when(callback?: (basemap: BasemapCompat) => void): Promise<BasemapCompat> {
    const basemap = await this.load();
    if (callback) {
      callback(basemap);
    }
    return basemap;
  }
}
