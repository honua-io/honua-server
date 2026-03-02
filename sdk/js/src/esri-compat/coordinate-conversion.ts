import { CompatEventBus, resolveCompatEventBus, safeInvokeCompatListener } from "./event-bus.js";

export type CoordinateFormatCompat = "lonlat" | "dms" | "dd";

export interface CoordinateConversionCompatOptions {
  view?: unknown;
  container?: unknown;
  eventBus?: CompatEventBus;
  formats?: readonly CoordinateFormatCompat[];
  mode?: "live" | "capture";
  multipleConversionsEnabled?: boolean;
}

export interface CoordinateConversionResultCompat {
  format: CoordinateFormatCompat;
  text: string;
}

export type CoordinateConversionLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface CoordinateConversionHandleCompat {
  remove(): void;
}

export class CoordinateConversionCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public loaded: boolean;
  public loadStatus: CoordinateConversionLoadStatusCompat;
  public formats: CoordinateFormatCompat[];
  public mode: "live" | "capture";
  public multipleConversionsEnabled: boolean;
  public location: [number, number] | undefined;
  public conversions: CoordinateConversionResultCompat[];
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: CoordinateConversionCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.formats = options.formats ? [...options.formats] : ["lonlat", "dms"];
    this.mode = options.mode ?? "live";
    this.multipleConversionsEnabled = options.multipleConversionsEnabled ?? true;
    this.location = undefined;
    this.conversions = [];
    this.watchListeners = new Map();
  }

  public async load(): Promise<CoordinateConversionCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("coordinate-conversion.loading", undefined, this);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("coordinate-conversion.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (widget: CoordinateConversionCompat) => void): Promise<CoordinateConversionCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): CoordinateConversionHandleCompat {
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

  public setLocation(location: [number, number]): readonly CoordinateConversionResultCompat[] {
    this.location = [location[0], location[1]];
    this.notifyWatchers("location", this.location);
    const conversions = this.formats.map((format) => ({
      format,
      text: formatCoordinate(location, format),
    }));
    this.conversions = this.multipleConversionsEnabled ? conversions : conversions.slice(0, 1);
    this.notifyWatchers("conversions", this.conversions);
    this.eventBus.emit("coordinate-conversion.updated", { location: this.location }, this);
    return this.conversions;
  }

  public addFormat(format: CoordinateFormatCompat): void {
    if (this.formats.includes(format)) {
      return;
    }
    this.formats.push(format);
    this.notifyWatchers("formats", this.formats);
    if (this.location) {
      this.setLocation(this.location);
    }
    this.eventBus.emit("coordinate-conversion.formats-updated", { formats: [...this.formats] }, this);
  }

  public removeFormat(format: CoordinateFormatCompat): boolean {
    const index = this.formats.indexOf(format);
    if (index < 0) {
      return false;
    }
    this.formats.splice(index, 1);
    this.notifyWatchers("formats", this.formats);
    if (this.location) {
      this.setLocation(this.location);
    }
    this.eventBus.emit("coordinate-conversion.formats-updated", { formats: [...this.formats] }, this);
    return true;
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

function formatCoordinate(location: [number, number], format: CoordinateFormatCompat): string {
  const [lon, lat] = location;
  if (format === "lonlat" || format === "dd") {
    return `${lat.toFixed(6)}, ${lon.toFixed(6)}`;
  }

  return `${toDms(lat, "N", "S")} ${toDms(lon, "E", "W")}`;
}

function toDms(value: number, positiveSuffix: string, negativeSuffix: string): string {
  const abs = Math.abs(value);
  const degrees = Math.trunc(abs);
  const minutesFloat = (abs - degrees) * 60;
  const minutes = Math.trunc(minutesFloat);
  const seconds = (minutesFloat - minutes) * 60;
  const suffix = value >= 0 ? positiveSuffix : negativeSuffix;
  return `${degrees}\u00B0${minutes}'${seconds.toFixed(2)}\"${suffix}`;
}
