import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";
import {
  RouteLayerCompat,
  type RouteLayerCompatOptions,
  type RouteSolveResultCompat,
  type RouteStopCompat,
} from "./route-layer.js";

export interface DirectionsCompatOptions {
  view?: unknown;
  container?: unknown;
  layer?: RouteLayerCompat;
  eventBus?: CompatEventBus;
  routeProvider?: RouteLayerCompatOptions["routeProvider"];
  stops?: readonly RouteStopCompat[];
  useDefaultRouteLayer?: boolean;
  showSaveAsButton?: boolean;
}

export interface DirectionsSolveSummaryCompat {
  stopCount: number;
  distanceMeters: number;
  durationSeconds: number;
}

export class DirectionsCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public readonly useDefaultRouteLayer: boolean;
  public readonly showSaveAsButton: boolean;
  public readonly layer: RouteLayerCompat;
  public route: RouteSolveResultCompat | undefined;

  public constructor(options: DirectionsCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view, options.layer) ?? new CompatEventBus();
    this.useDefaultRouteLayer = options.useDefaultRouteLayer ?? true;
    this.showSaveAsButton = options.showSaveAsButton ?? false;
    this.layer =
      options.layer ??
      new RouteLayerCompat({
        stops: options.stops,
        routeProvider: options.routeProvider,
        eventBus: this.eventBus,
      });
    this.route = undefined;
  }

  public setStops(stops: readonly RouteStopCompat[]): void {
    this.layer.clearStops();
    this.layer.addStops(stops);
    this.eventBus.emit("directions.stops-updated", { stopCount: this.layer.stops.length }, this);
  }

  public addStop(stop: RouteStopCompat): void {
    this.layer.addStop(stop);
    this.eventBus.emit("directions.stops-updated", { stopCount: this.layer.stops.length }, this);
  }

  public clearStops(): void {
    this.layer.clearStops();
    this.route = undefined;
    this.eventBus.emit("directions.stops-cleared", undefined, this);
  }

  public async solve(): Promise<RouteSolveResultCompat | undefined> {
    this.eventBus.emit("directions.solve-started", { stopCount: this.layer.stops.length }, this);
    try {
      const route = await this.layer.solve();
      this.route = route;
      this.eventBus.emit("directions.solve-completed", { route }, this);
      return route;
    } catch (error) {
      this.route = undefined;
      this.eventBus.emit("directions.solve-error", { error }, this);
      throw error;
    }
  }

  public getSummary(): DirectionsSolveSummaryCompat | undefined {
    if (!this.route) {
      return undefined;
    }
    return {
      stopCount: this.layer.stops.length,
      distanceMeters: this.route.totalLengthMeters,
      durationSeconds: this.route.totalTimeSeconds,
    };
  }
}
