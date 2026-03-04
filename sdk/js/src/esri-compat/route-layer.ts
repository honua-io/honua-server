import { CompatEventBus, resolveCompatEventBus, safeInvokeCompatListener } from "./event-bus.js";
import { normalizeInsertIndex, normalizeOpacity } from "./utils.js";

export interface RouteStopCompat {
  name?: string;
  location: [number, number];
}

export interface RouteSolveResultCompat {
  path: [number, number][];
  totalLengthMeters: number;
  totalTimeSeconds: number;
}

export interface RouteLayerCompatOptions {
  id?: string;
  title?: string;
  url?: string;
  visible?: boolean;
  opacity?: number;
  listMode?: "show" | "hide";
  stops?: readonly RouteStopCompat[];
  autoSolve?: boolean;
  routeProvider?: (
    stops: readonly RouteStopCompat[],
    options: RouteLayerCompat,
  ) => Promise<RouteSolveResultCompat> | RouteSolveResultCompat;
  eventBus?: CompatEventBus;
}

export type RouteLayerLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface RouteLayerHandleCompat {
  remove(): void;
}

export class RouteLayerCompat {
  public readonly type: "route";
  public id: string | undefined;
  public title: string | undefined;
  public url: string | undefined;
  public visible: boolean;
  public opacity: number;
  public listMode: "show" | "hide";
  public readonly eventBus: CompatEventBus;
  public route: RouteSolveResultCompat | undefined;
  public readonly autoSolve: boolean;
  public solving: boolean;
  public loaded: boolean;
  public loadStatus: RouteLayerLoadStatusCompat;

  private readonly stopsInternal: RouteStopCompat[];
  private readonly routeProvider: (
    stops: readonly RouteStopCompat[],
    options: RouteLayerCompat,
  ) => Promise<RouteSolveResultCompat> | RouteSolveResultCompat;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: RouteLayerCompatOptions = {}) {
    this.type = "route";
    this.id = options.id;
    this.title = options.title;
    this.url = options.url;
    this.visible = options.visible ?? true;
    this.opacity = options.opacity ?? 1;
    this.listMode = options.listMode ?? "show";
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.stops) ?? new CompatEventBus();
    this.route = undefined;
    this.autoSolve = options.autoSolve ?? false;
    this.solving = false;
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.stopsInternal = options.stops ? options.stops.map(cloneRouteStop) : [];
    this.routeProvider = options.routeProvider ?? defaultRouteProvider;
    this.watchListeners = new Map();
  }

  public get stops(): readonly RouteStopCompat[] {
    return this.stopsInternal;
  }

  public async load(): Promise<RouteLayerCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("route-layer.loading", { layerId: this.id }, this);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("route-layer.loaded", { layerId: this.id }, this);
    return this;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): RouteLayerHandleCompat {
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

  public addStop(stop: RouteStopCompat, index?: number): void {
    const normalized = cloneRouteStop(stop);
    if (index === undefined) {
      this.stopsInternal.push(normalized);
      this.eventBus.emit(
        "route-layer.stop-added",
        { layerId: this.id, stop: normalized, index: this.stopsInternal.length - 1 },
        this,
      );
    } else {
      const insertAt = normalizeInsertIndex(index, this.stopsInternal.length);
      this.stopsInternal.splice(insertAt, 0, normalized);
      this.eventBus.emit("route-layer.stop-added", { layerId: this.id, stop: normalized, index: insertAt }, this);
    }
    this.notifyWatchers("stops", this.stops);

    if (this.autoSolve) {
      void this.solve();
    }
  }

  public addStops(stops: readonly RouteStopCompat[], index?: number): void {
    if (stops.length === 0) {
      return;
    }

    const normalized = stops.map(cloneRouteStop);
    if (index === undefined) {
      const startIndex = this.stopsInternal.length;
      this.stopsInternal.push(...normalized);
      this.eventBus.emit("route-layer.stops-added", { layerId: this.id, stops: normalized, index: startIndex }, this);
    } else {
      const insertAt = normalizeInsertIndex(index, this.stopsInternal.length);
      this.stopsInternal.splice(insertAt, 0, ...normalized);
      this.eventBus.emit("route-layer.stops-added", { layerId: this.id, stops: normalized, index: insertAt }, this);
    }
    this.notifyWatchers("stops", this.stops);

    if (this.autoSolve) {
      void this.solve();
    }
  }

  public clearStops(): void {
    if (this.stopsInternal.length === 0) {
      return;
    }

    const removed = [...this.stopsInternal];
    this.stopsInternal.length = 0;
    this.route = undefined;
    this.notifyWatchers("stops", this.stops);
    this.notifyWatchers("route", this.route);
    this.eventBus.emit("route-layer.stops-cleared", { layerId: this.id, stops: removed }, this);
  }

  public async solve(): Promise<RouteSolveResultCompat | undefined> {
    if (this.stopsInternal.length < 2) {
      this.route = undefined;
      this.notifyWatchers("route", this.route);
      return undefined;
    }

    this.solving = true;
    this.notifyWatchers("solving", this.solving);
    this.eventBus.emit("route-layer.solve-started", { layerId: this.id, stopCount: this.stopsInternal.length }, this);
    try {
      const result = await this.routeProvider([...this.stopsInternal], this);
      this.route = {
        path: result.path.map((point) => [point[0], point[1]]),
        totalLengthMeters: result.totalLengthMeters,
        totalTimeSeconds: result.totalTimeSeconds,
      };
      this.notifyWatchers("route", this.route);
      this.eventBus.emit("route-layer.solve-completed", { layerId: this.id, route: this.route }, this);
      return this.route;
    } catch (error) {
      this.route = undefined;
      this.notifyWatchers("route", this.route);
      this.eventBus.emit("route-layer.solve-error", { layerId: this.id, error }, this);
      throw error;
    } finally {
      this.solving = false;
      this.notifyWatchers("solving", this.solving);
    }
  }

  public async refresh(): Promise<RouteSolveResultCompat | undefined> {
    return this.solve();
  }

  public async when(callback?: (layer: RouteLayerCompat) => void): Promise<RouteLayerCompat> {
    await this.load();
    if (callback) {
      callback(this);
    }
    return this;
  }

  public destroy(): void {
    this.watchListeners.clear();
    this.eventBus.emit("route-layer.destroyed", { layerId: this.id }, this);
  }

  public setVisibility(visible: boolean): void {
    this.visible = visible;
    this.notifyWatchers("visible", this.visible);
    this.eventBus.emit("layer.visibility-changed", { layerId: this.id, visible }, this);
  }

  public setOpacity(opacity: number): void {
    this.opacity = normalizeOpacity(opacity);
    this.notifyWatchers("opacity", this.opacity);
    this.eventBus.emit("layer.opacity-changed", { layerId: this.id, opacity: this.opacity }, this);
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

function cloneRouteStop(stop: RouteStopCompat): RouteStopCompat {
  return {
    name: stop.name,
    location: [stop.location[0], stop.location[1]],
  };
}

function defaultRouteProvider(stops: readonly RouteStopCompat[]): RouteSolveResultCompat {
  const path: [number, number][] = stops.map((stop) => [stop.location[0], stop.location[1]]);
  let totalLengthMeters = 0;
  for (let i = 1; i < path.length; i += 1) {
    totalLengthMeters += haversineDistanceMeters(path[i - 1], path[i]);
  }

  const averageMetersPerSecond = 13.4112; // ~30mph
  const totalTimeSeconds = totalLengthMeters / averageMetersPerSecond;
  return {
    path,
    totalLengthMeters,
    totalTimeSeconds,
  };
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

function toRadians(value: number): number {
  return (value * Math.PI) / 180;
}

