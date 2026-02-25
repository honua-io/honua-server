import { MapCompat, type MapCompatOptions } from "./map.js";

export interface WebMapCompatOptions extends MapCompatOptions {
  portalItem?: unknown;
}

export class WebMapCompat extends MapCompat {
  public portalItem: unknown;
  public loaded: boolean;

  public constructor(options: WebMapCompatOptions = {}) {
    super(options);
    this.portalItem = options.portalItem;
    this.loaded = false;
  }

  public async load(): Promise<WebMapCompat> {
    this.loaded = true;
    return this;
  }

  public async when(callback?: (map: WebMapCompat) => void): Promise<WebMapCompat> {
    const map = await this.load();
    if (callback) {
      callback(map);
    }
    return map;
  }
}
