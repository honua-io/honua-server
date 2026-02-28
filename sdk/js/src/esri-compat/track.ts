import { CompatEventBus, resolveCompatEventBus, safeInvokeCompatListener } from "./event-bus.js";

export interface TrackPositionCompat {
  coords: {
    latitude: number;
    longitude: number;
    accuracy?: number;
    heading?: number | null;
    speed?: number | null;
  };
  timestamp?: number;
}

export interface TrackCompatOptions {
  view?: unknown;
  container?: unknown;
  eventBus?: CompatEventBus;
  tracking?: boolean;
  goToLocationEnabled?: boolean;
  useHeadingEnabled?: boolean;
  rotationEnabled?: boolean;
  scale?: number;
  trackProvider?: () => Promise<TrackPositionCompat>;
}

export type TrackLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface TrackHandleCompat {
  remove(): void;
}

export class TrackCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public loaded: boolean;
  public loadStatus: TrackLoadStatusCompat;
  public tracking: boolean;
  public readonly goToLocationEnabled: boolean;
  public readonly useHeadingEnabled: boolean;
  public readonly rotationEnabled: boolean;
  public readonly scale: number | undefined;
  public position: TrackPositionCompat | undefined;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  private readonly trackProvider: () => Promise<TrackPositionCompat>;

  public constructor(options: TrackCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.tracking = options.tracking ?? false;
    this.goToLocationEnabled = options.goToLocationEnabled ?? true;
    this.useHeadingEnabled = options.useHeadingEnabled ?? false;
    this.rotationEnabled = options.rotationEnabled ?? false;
    this.scale = options.scale;
    this.position = undefined;
    this.trackProvider = options.trackProvider ?? getDefaultTrackProvider();
    this.watchListeners = new Map();
  }

  public async load(): Promise<TrackCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("track.loading", undefined, this);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("track.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (widget: TrackCompat) => void): Promise<TrackCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): TrackHandleCompat {
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

  public async start(): Promise<TrackPositionCompat> {
    this.tracking = true;
    this.notifyWatchers("tracking", this.tracking);
    this.eventBus.emit("track.start", undefined, this);

    try {
      const position = await this.trackProvider();
      this.position = position;
      this.notifyWatchers("position", this.position);
      const center: [number, number] = [position.coords.longitude, position.coords.latitude];

      if (this.goToLocationEnabled) {
        const target: { center: [number, number]; scale?: number } = { center };
        if (typeof this.scale === "number" && Number.isFinite(this.scale)) {
          target.scale = this.scale;
        }

        if (isGoToProvider(this.view)) {
          await this.view.goTo(target);
        } else {
          applyViewCenterScale(this.view, target);
        }
      }

      if (
        this.rotationEnabled &&
        this.useHeadingEnabled &&
        typeof position.coords.heading === "number" &&
        Number.isFinite(position.coords.heading) &&
        isRecord(this.view)
      ) {
        this.view.rotation = position.coords.heading;
      }

      this.eventBus.emit(
        "track.position",
        {
          position,
          center,
          tracking: this.tracking,
        },
        this,
      );
      return position;
    } catch (error) {
      this.tracking = false;
      this.notifyWatchers("tracking", this.tracking);
      this.eventBus.emit("track.error", { error }, this);
      throw error;
    }
  }

  public stop(): void {
    if (!this.tracking) {
      return;
    }
    this.tracking = false;
    this.notifyWatchers("tracking", this.tracking);
    this.eventBus.emit("track.stop", undefined, this);
  }

  public async toggle(force?: boolean): Promise<boolean> {
    const next = force ?? !this.tracking;
    if (!next) {
      this.stop();
      return this.tracking;
    }

    await this.start();
    return this.tracking;
  }

  public destroy(): void {
    this.watchListeners.clear();
  }

  private notifyWatchers(propertyName: string, value: unknown): void {
    const listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      safeInvokeCompatListener(listener, value);
    }
  }
}

interface GoToProvider {
  goTo(target: { center?: unknown; scale?: number }): Promise<unknown> | unknown;
}

function isGoToProvider(value: unknown): value is GoToProvider {
  return isRecord(value) && typeof value.goTo === "function";
}

function applyViewCenterScale(view: unknown, target: { center: [number, number]; scale?: number }): void {
  if (!isRecord(view)) {
    return;
  }
  view.center = target.center;
  if (typeof target.scale === "number") {
    view.scale = target.scale;
  }
}

function getDefaultTrackProvider(): () => Promise<TrackPositionCompat> {
  return () =>
    new Promise<TrackPositionCompat>((resolve, reject) => {
      const geolocation = globalThis.navigator?.geolocation;
      if (!geolocation || typeof geolocation.getCurrentPosition !== "function") {
        reject(new Error("Geolocation API is unavailable; provide trackProvider."));
        return;
      }

      geolocation.getCurrentPosition(
        (position) => {
          resolve({
            coords: {
              latitude: position.coords.latitude,
              longitude: position.coords.longitude,
              accuracy: position.coords.accuracy,
              heading: position.coords.heading,
              speed: position.coords.speed,
            },
            timestamp: position.timestamp,
          });
        },
        (error) => {
          reject(error);
        },
      );
    });
}

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null;
}
