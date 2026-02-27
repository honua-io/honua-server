import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";

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

export class CoordinateConversionCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public formats: CoordinateFormatCompat[];
  public mode: "live" | "capture";
  public multipleConversionsEnabled: boolean;
  public location: [number, number] | undefined;
  public conversions: CoordinateConversionResultCompat[];

  public constructor(options: CoordinateConversionCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.formats = options.formats ? [...options.formats] : ["lonlat", "dms"];
    this.mode = options.mode ?? "live";
    this.multipleConversionsEnabled = options.multipleConversionsEnabled ?? true;
    this.location = undefined;
    this.conversions = [];
  }

  public setLocation(location: [number, number]): readonly CoordinateConversionResultCompat[] {
    this.location = [location[0], location[1]];
    const conversions = this.formats.map((format) => ({
      format,
      text: formatCoordinate(location, format),
    }));
    this.conversions = this.multipleConversionsEnabled ? conversions : conversions.slice(0, 1);
    this.eventBus.emit("coordinate-conversion.updated", { location: this.location }, this);
    return this.conversions;
  }

  public addFormat(format: CoordinateFormatCompat): void {
    if (this.formats.includes(format)) {
      return;
    }
    this.formats.push(format);
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
    if (this.location) {
      this.setLocation(this.location);
    }
    this.eventBus.emit("coordinate-conversion.formats-updated", { formats: [...this.formats] }, this);
    return true;
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
