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

export interface MapViewHandle {
  remove(): void;
}

export class MapViewCompat {
  public map: unknown;
  public container: unknown;
  public center: unknown;
  public zoom: number | undefined;

  private readonly eventListeners: Map<string, Set<(event: unknown) => void>>;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;
  private readonly readyPromise: Promise<MapViewCompat>;

  public constructor(options: MapViewCompatOptions = {}) {
    this.map = options.map;
    this.container = options.container;
    this.center = options.center;
    this.zoom = options.zoom;
    this.eventListeners = new Map();
    this.watchListeners = new Map();
    this.readyPromise = Promise.resolve(this);
  }

  public async when(callback?: (view: MapViewCompat) => void): Promise<MapViewCompat> {
    const view = await this.readyPromise;
    if (callback) {
      callback(view);
    }

    return view;
  }

  public async goTo(target: MapViewGoToTarget): Promise<MapViewCompat> {
    if (target.center !== undefined) {
      this.center = target.center;
      this.notifyWatchers("center", this.center);
    }
    if (target.zoom !== undefined) {
      this.zoom = target.zoom;
      this.notifyWatchers("zoom", this.zoom);
    }
    this.emit("go-to", target);

    return this;
  }

  public on(eventName: string, listener: (event: unknown) => void): MapViewHandle {
    let listeners = this.eventListeners.get(eventName);
    if (!listeners) {
      listeners = new Set();
      this.eventListeners.set(eventName, listeners);
    }
    listeners.add(listener);

    return {
      remove: () => {
        listeners?.delete(listener);
      },
    };
  }

  public watch(propertyName: string, listener: (value: unknown) => void): MapViewHandle {
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

  public destroy(): void {
    this.emit("destroy", undefined);
    this.map = undefined;
    this.notifyWatchers("map", this.map);
    this.container = undefined;
    this.notifyWatchers("container", this.container);
    this.center = undefined;
    this.notifyWatchers("center", this.center);
    this.zoom = undefined;
    this.notifyWatchers("zoom", this.zoom);
    this.eventListeners.clear();
    this.watchListeners.clear();
  }

  private emit(eventName: string, payload: unknown): void {
    const listeners = this.eventListeners.get(eventName);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      listener(payload);
    }
  }

  private notifyWatchers(propertyName: string, value: unknown): void {
    const listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      listener(value);
    }
  }
}
