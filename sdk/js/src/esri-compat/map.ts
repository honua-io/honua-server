export interface MapCompatOptions {
  basemap?: unknown;
  layers?: unknown;
}

export class MapCompat {
  public basemap: unknown;
  private readonly layersInternal: unknown[];

  public constructor(options: MapCompatOptions = {}) {
    this.basemap = options.basemap;
    this.layersInternal = Array.isArray(options.layers) ? [...options.layers] : [];
  }

  public get layers(): readonly unknown[] {
    return this.layersInternal;
  }

  public add(layer: unknown): void {
    this.layersInternal.push(layer);
  }

  public addMany(layers: readonly unknown[]): void {
    this.layersInternal.push(...layers);
  }

  public remove(layer: unknown): boolean {
    const index = this.layersInternal.indexOf(layer);
    if (index < 0) {
      return false;
    }

    this.layersInternal.splice(index, 1);
    return true;
  }
}
