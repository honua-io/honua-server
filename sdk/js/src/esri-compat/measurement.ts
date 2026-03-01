import { CompatEventBus, resolveCompatEventBus, safeInvokeCompatListener } from "./event-bus.js";

export type MeasurementToolCompat = "distance" | "area" | "direct-line";
export type LinearUnitCompat = "meters" | "kilometers" | "feet" | "miles";
export type AreaUnitCompat = "square-meters" | "square-kilometers" | "square-feet" | "square-miles";

export interface MeasurementCompatOptions {
  view?: unknown;
  container?: unknown;
  eventBus?: CompatEventBus;
  activeTool?: MeasurementToolCompat;
  linearUnit?: LinearUnitCompat;
  areaUnit?: AreaUnitCompat;
}

export interface MeasurementResultCompat {
  tool: MeasurementToolCompat;
  value: number;
  unit: LinearUnitCompat | AreaUnitCompat;
}

export type MeasurementLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface MeasurementHandleCompat {
  remove(): void;
}

export class MeasurementCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public loaded: boolean;
  public loadStatus: MeasurementLoadStatusCompat;
  public activeTool: MeasurementToolCompat | undefined;
  public linearUnit: LinearUnitCompat;
  public areaUnit: AreaUnitCompat;
  public lastMeasurement: MeasurementResultCompat | undefined;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: MeasurementCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.activeTool = options.activeTool;
    this.linearUnit = options.linearUnit ?? "meters";
    this.areaUnit = options.areaUnit ?? "square-meters";
    this.lastMeasurement = undefined;
    this.watchListeners = new Map();
  }

  public async load(): Promise<MeasurementCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("measurement.loading", undefined, this);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("measurement.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (widget: MeasurementCompat) => void): Promise<MeasurementCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): MeasurementHandleCompat {
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

  public start(tool: MeasurementToolCompat): void {
    this.activeTool = tool;
    this.notifyWatchers("activeTool", this.activeTool);
    this.eventBus.emit("measurement.started", { tool }, this);
  }

  public stop(): void {
    if (!this.activeTool) {
      return;
    }
    const tool = this.activeTool;
    this.activeTool = undefined;
    this.notifyWatchers("activeTool", this.activeTool);
    this.eventBus.emit("measurement.stopped", { tool }, this);
  }

  public clear(): void {
    this.lastMeasurement = undefined;
    this.notifyWatchers("lastMeasurement", this.lastMeasurement);
    this.eventBus.emit("measurement.cleared", undefined, this);
  }

  public measureDistance(points: readonly [number, number][]): MeasurementResultCompat {
    const meters = measureLineInMeters(points);
    const value = convertLinearUnit(meters, this.linearUnit);
    const result: MeasurementResultCompat = {
      tool: "distance",
      value,
      unit: this.linearUnit,
    };
    this.lastMeasurement = result;
    this.notifyWatchers("lastMeasurement", this.lastMeasurement);
    this.eventBus.emit("measurement.updated", result, this);
    return result;
  }

  public measureArea(ring: readonly [number, number][]): MeasurementResultCompat {
    const squareMeters = measurePolygonInSquareMeters(ring);
    const value = convertAreaUnit(squareMeters, this.areaUnit);
    const result: MeasurementResultCompat = {
      tool: "area",
      value,
      unit: this.areaUnit,
    };
    this.lastMeasurement = result;
    this.notifyWatchers("lastMeasurement", this.lastMeasurement);
    this.eventBus.emit("measurement.updated", result, this);
    return result;
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

function measureLineInMeters(points: readonly [number, number][]): number {
  if (points.length < 2) {
    return 0;
  }

  let total = 0;
  for (let i = 1; i < points.length; i += 1) {
    total += haversineDistanceMeters(points[i - 1], points[i]);
  }
  return total;
}

function haversineDistanceMeters(a: [number, number], b: [number, number]): number {
  const [lonA, latA] = a;
  const [lonB, latB] = b;
  const dLat = toRadians(latB - latA);
  const dLon = toRadians(lonB - lonA);
  const latARad = toRadians(latA);
  const latBRad = toRadians(latB);
  const h =
    Math.sin(dLat / 2) * Math.sin(dLat / 2) +
    Math.cos(latARad) * Math.cos(latBRad) * Math.sin(dLon / 2) * Math.sin(dLon / 2);
  const c = 2 * Math.atan2(Math.sqrt(h), Math.sqrt(1 - h));
  return 6371008.8 * c;
}

function measurePolygonInSquareMeters(ring: readonly [number, number][]): number {
  if (ring.length < 3) {
    return 0;
  }

  const closed = isClosedRing(ring) ? ring : [...ring, ring[0]];
  const lat0 = closed.reduce((sum, [, lat]) => sum + lat, 0) / closed.length;
  const metersPerDegLat = 111132.92;
  const metersPerDegLon = 111412.84 * Math.cos(toRadians(lat0));

  let sum = 0;
  for (let i = 1; i < closed.length; i += 1) {
    const [lonA, latA] = closed[i - 1];
    const [lonB, latB] = closed[i];
    const xA = lonA * metersPerDegLon;
    const yA = latA * metersPerDegLat;
    const xB = lonB * metersPerDegLon;
    const yB = latB * metersPerDegLat;
    sum += xA * yB - xB * yA;
  }

  return Math.abs(sum) / 2;
}

function isClosedRing(ring: readonly [number, number][]): boolean {
  if (ring.length < 2) {
    return false;
  }
  const first = ring[0];
  const last = ring[ring.length - 1];
  return first[0] === last[0] && first[1] === last[1];
}

function convertLinearUnit(valueMeters: number, unit: LinearUnitCompat): number {
  switch (unit) {
    case "meters":
      return valueMeters;
    case "kilometers":
      return valueMeters / 1000;
    case "feet":
      return valueMeters * 3.280839895;
    case "miles":
      return valueMeters / 1609.344;
  }
}

function convertAreaUnit(valueSquareMeters: number, unit: AreaUnitCompat): number {
  switch (unit) {
    case "square-meters":
      return valueSquareMeters;
    case "square-kilometers":
      return valueSquareMeters / 1_000_000;
    case "square-feet":
      return valueSquareMeters * 10.763910417;
    case "square-miles":
      return valueSquareMeters / 2_589_988.110336;
  }
}

function toRadians(value: number): number {
  return (value * Math.PI) / 180;
}
