import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";

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

export class TrackCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public tracking: boolean;
  public readonly goToLocationEnabled: boolean;
  public readonly useHeadingEnabled: boolean;
  public readonly rotationEnabled: boolean;
  public readonly scale: number | undefined;
  public position: TrackPositionCompat | undefined;

  private readonly trackProvider: () => Promise<TrackPositionCompat>;

  public constructor(options: TrackCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.tracking = options.tracking ?? false;
    this.goToLocationEnabled = options.goToLocationEnabled ?? true;
    this.useHeadingEnabled = options.useHeadingEnabled ?? false;
    this.rotationEnabled = options.rotationEnabled ?? false;
    this.scale = options.scale;
    this.position = undefined;
    this.trackProvider = options.trackProvider ?? getDefaultTrackProvider();
  }

  public async start(): Promise<TrackPositionCompat> {
    this.tracking = true;
    this.eventBus.emit("track.start", undefined, this);

    try {
      const position = await this.trackProvider();
      this.position = position;
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
      this.eventBus.emit("track.error", { error }, this);
      throw error;
    }
  }

  public stop(): void {
    if (!this.tracking) {
      return;
    }
    this.tracking = false;
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
