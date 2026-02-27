import { CompatEventBus } from "./event-bus.js";
import {
  MeasurementCompat,
  type AreaUnitCompat,
  type LinearUnitCompat,
  type MeasurementResultCompat,
} from "./measurement.js";

export interface DistanceMeasurement2DCompatOptions {
  view?: unknown;
  container?: unknown;
  eventBus?: CompatEventBus;
  unit?: LinearUnitCompat;
}

export interface AreaMeasurement2DCompatOptions {
  view?: unknown;
  container?: unknown;
  eventBus?: CompatEventBus;
  unit?: AreaUnitCompat;
}

export type DistanceMeasurement2DLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface DistanceMeasurement2DHandleCompat {
  remove(): void;
}

export type AreaMeasurement2DLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface AreaMeasurement2DHandleCompat {
  remove(): void;
}

export class DistanceMeasurement2DCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public loaded: boolean;
  public loadStatus: DistanceMeasurement2DLoadStatusCompat;
  public unit: LinearUnitCompat;
  public lastMeasurement: MeasurementResultCompat | undefined;
  public readonly measurement: MeasurementCompat;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: DistanceMeasurement2DCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.measurement = new MeasurementCompat({
      view: options.view,
      container: options.container,
      eventBus: options.eventBus,
      activeTool: "distance",
      linearUnit: options.unit,
    });
    this.eventBus = this.measurement.eventBus;
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.unit = this.measurement.linearUnit;
    this.lastMeasurement = undefined;
    this.watchListeners = new Map();
  }

  public async load(): Promise<DistanceMeasurement2DCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("distance-measurement-2d.loading", undefined, this);
    await this.measurement.load();
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("distance-measurement-2d.loaded", undefined, this);
    return this;
  }

  public async when(
    callback?: (widget: DistanceMeasurement2DCompat) => void,
  ): Promise<DistanceMeasurement2DCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(
    propertyName: string,
    listener: (value: unknown) => void,
  ): DistanceMeasurement2DHandleCompat {
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

  public clear(): void {
    this.measurement.clear();
    this.lastMeasurement = undefined;
    this.notifyWatchers("lastMeasurement", this.lastMeasurement);
    this.eventBus.emit("distance-measurement-2d.cleared", undefined, this);
  }

  public measure(points: readonly [number, number][]): MeasurementResultCompat {
    this.measurement.start("distance");
    const result = this.measurement.measureDistance(points);
    this.unit = this.measurement.linearUnit;
    this.notifyWatchers("unit", this.unit);
    this.lastMeasurement = result;
    this.notifyWatchers("lastMeasurement", this.lastMeasurement);
    this.eventBus.emit("distance-measurement-2d.updated", result, this);
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
      listener(value);
    }
  }
}

export class AreaMeasurement2DCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public loaded: boolean;
  public loadStatus: AreaMeasurement2DLoadStatusCompat;
  public unit: AreaUnitCompat;
  public lastMeasurement: MeasurementResultCompat | undefined;
  public readonly measurement: MeasurementCompat;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: AreaMeasurement2DCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.measurement = new MeasurementCompat({
      view: options.view,
      container: options.container,
      eventBus: options.eventBus,
      activeTool: "area",
      areaUnit: options.unit,
    });
    this.eventBus = this.measurement.eventBus;
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.unit = this.measurement.areaUnit;
    this.lastMeasurement = undefined;
    this.watchListeners = new Map();
  }

  public async load(): Promise<AreaMeasurement2DCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("area-measurement-2d.loading", undefined, this);
    await this.measurement.load();
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("area-measurement-2d.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (widget: AreaMeasurement2DCompat) => void): Promise<AreaMeasurement2DCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(
    propertyName: string,
    listener: (value: unknown) => void,
  ): AreaMeasurement2DHandleCompat {
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

  public clear(): void {
    this.measurement.clear();
    this.lastMeasurement = undefined;
    this.notifyWatchers("lastMeasurement", this.lastMeasurement);
    this.eventBus.emit("area-measurement-2d.cleared", undefined, this);
  }

  public measure(ring: readonly [number, number][]): MeasurementResultCompat {
    this.measurement.start("area");
    const result = this.measurement.measureArea(ring);
    this.unit = this.measurement.areaUnit;
    this.notifyWatchers("unit", this.unit);
    this.lastMeasurement = result;
    this.notifyWatchers("lastMeasurement", this.lastMeasurement);
    this.eventBus.emit("area-measurement-2d.updated", result, this);
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
      listener(value);
    }
  }
}
