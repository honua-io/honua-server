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

export type DirectionsLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface DirectionsHandleCompat {
  remove(): void;
}

export class DirectionsCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public loaded: boolean;
  public loadStatus: DirectionsLoadStatusCompat;
  public readonly useDefaultRouteLayer: boolean;
  public readonly showSaveAsButton: boolean;
  public readonly layer: RouteLayerCompat;
  public route: RouteSolveResultCompat | undefined;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: DirectionsCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view, options.layer) ?? new CompatEventBus();
    this.loaded = false;
    this.loadStatus = "not-loaded";
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
    this.watchListeners = new Map();
  }

  public async load(): Promise<DirectionsCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("directions.loading", undefined, this);
    await this.layer.load();
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("directions.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (widget: DirectionsCompat) => void): Promise<DirectionsCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): DirectionsHandleCompat {
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

  public setStops(stops: readonly RouteStopCompat[]): void {
    this.layer.clearStops();
    this.layer.addStops(stops);
    this.notifyWatchers("stops", this.layer.stops);
    this.eventBus.emit("directions.stops-updated", { stopCount: this.layer.stops.length }, this);
  }

  public addStop(stop: RouteStopCompat): void {
    this.layer.addStop(stop);
    this.notifyWatchers("stops", this.layer.stops);
    this.eventBus.emit("directions.stops-updated", { stopCount: this.layer.stops.length }, this);
  }

  public clearStops(): void {
    this.layer.clearStops();
    this.notifyWatchers("stops", this.layer.stops);
    this.route = undefined;
    this.notifyWatchers("route", this.route);
    this.eventBus.emit("directions.stops-cleared", undefined, this);
  }

  public async solve(): Promise<RouteSolveResultCompat | undefined> {
    this.eventBus.emit("directions.solve-started", { stopCount: this.layer.stops.length }, this);
    try {
      const route = await this.layer.solve();
      this.route = route;
      this.notifyWatchers("route", this.route);
      this.eventBus.emit("directions.solve-completed", { route }, this);
      return route;
    } catch (error) {
      this.route = undefined;
      this.notifyWatchers("route", this.route);
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
