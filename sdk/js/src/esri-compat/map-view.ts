export interface MapViewCompatOptions {
  map?: unknown;
  container?: unknown;
  center?: unknown;
  zoom?: number;
}

export interface MapViewGoToTarget {
  center?: unknown;
  zoom?: number;
}

export class MapViewCompat {
  public map: unknown;
  public container: unknown;
  public center: unknown;
  public zoom: number | undefined;

  private readonly readyPromise: Promise<MapViewCompat>;

  public constructor(options: MapViewCompatOptions = {}) {
    this.map = options.map;
    this.container = options.container;
    this.center = options.center;
    this.zoom = options.zoom;
    this.readyPromise = Promise.resolve(this);
  }

  public when(): Promise<MapViewCompat> {
    return this.readyPromise;
  }

  public async goTo(target: MapViewGoToTarget): Promise<MapViewCompat> {
    if (target.center !== undefined) {
      this.center = target.center;
    }
    if (target.zoom !== undefined) {
      this.zoom = target.zoom;
    }

    return this;
  }

  public destroy(): void {
    this.map = undefined;
    this.container = undefined;
    this.center = undefined;
  }
}
