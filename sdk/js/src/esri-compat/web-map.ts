import {
  MapCompat,
  type MapCompatHandle,
  type MapCompatOptions,
  type MapLoadStatusCompat,
} from "./map.js";

export interface WebMapCompatOptions extends MapCompatOptions {
  portalItem?: unknown;
}

export type WebMapLoadStatusCompat = MapLoadStatusCompat;
export type WebMapHandleCompat = MapCompatHandle;

export class WebMapCompat extends MapCompat {
  public constructor(options: WebMapCompatOptions = {}) {
    super(options);
  }

  public async load(): Promise<WebMapCompat> {
    await super.load();
    return this;
  }

  public async when(callback?: (map: WebMapCompat) => void): Promise<WebMapCompat> {
    const map = await this.load();
    if (callback) {
      callback(map);
    }
    return map;
  }

  public setPortalItem(portalItem: unknown): void {
    super.setPortalItem(portalItem);
    this.eventBus.emit("web-map.portal-item-changed", { portalItem }, this);
  }
}
