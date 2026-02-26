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

export class DistanceMeasurement2DCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public unit: LinearUnitCompat;
  public readonly measurement: MeasurementCompat;

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
    this.unit = this.measurement.linearUnit;
  }

  public clear(): void {
    this.measurement.clear();
    this.eventBus.emit("distance-measurement-2d.cleared", undefined, this);
  }

  public measure(points: readonly [number, number][]): MeasurementResultCompat {
    this.measurement.start("distance");
    const result = this.measurement.measureDistance(points);
    this.unit = this.measurement.linearUnit;
    this.eventBus.emit("distance-measurement-2d.updated", result, this);
    return result;
  }
}

export class AreaMeasurement2DCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public unit: AreaUnitCompat;
  public readonly measurement: MeasurementCompat;

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
    this.unit = this.measurement.areaUnit;
  }

  public clear(): void {
    this.measurement.clear();
    this.eventBus.emit("area-measurement-2d.cleared", undefined, this);
  }

  public measure(ring: readonly [number, number][]): MeasurementResultCompat {
    this.measurement.start("area");
    const result = this.measurement.measureArea(ring);
    this.unit = this.measurement.areaUnit;
    this.eventBus.emit("area-measurement-2d.updated", result, this);
    return result;
  }
}
