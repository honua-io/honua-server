import { CompatEventBus } from "./event-bus.js";
import {
  RouteLayerCompat,
  type RouteLayerCompatOptions,
  type RouteSolveResultCompat,
  type RouteStopCompat,
} from "./route-layer.js";

export interface RouteTaskCompatOptions {
  url?: string;
  apiKey?: string;
  requestOptions?: Record<string, unknown>;
  eventBus?: CompatEventBus;
  routeProvider?: RouteLayerCompatOptions["routeProvider"];
}

export interface RouteTaskStopFeatureCompat {
  geometry?: {
    x?: number;
    y?: number;
    longitude?: number;
    latitude?: number;
  };
  attributes?: Record<string, unknown>;
}

export interface RouteTaskStopsFeatureSetCompat {
  features?: readonly RouteTaskStopFeatureCompat[];
}

export interface RouteTaskSolveParametersCompat {
  stops?: readonly RouteStopCompat[] | RouteTaskStopsFeatureSetCompat;
  returnDirections?: boolean;
}

export interface RouteTaskDirectionsFeatureCompat {
  attributes: {
    text: string;
    length: number;
    time: number;
  };
  geometry: {
    paths: [number, number][][];
    spatialReference: {
      wkid: number;
    };
  };
}

export interface RouteTaskDirectionsSummaryCompat {
  totalLength: number;
  totalTime: number;
  features: readonly RouteTaskDirectionsFeatureCompat[];
}

export interface RouteTaskResultGraphicCompat {
  geometry: {
    paths: [number, number][][];
    spatialReference: {
      wkid: number;
    };
  };
  attributes: {
    Total_Kilometers: number;
    Total_Miles: number;
    Total_TravelTime: number;
    Total_Length: number;
  };
}

export interface RouteTaskRouteResultCompat {
  route: RouteTaskResultGraphicCompat;
  directions?: RouteTaskDirectionsSummaryCompat;
  stops: readonly RouteStopCompat[];
}

export interface RouteTaskSolveResultCompat {
  routeResults: readonly RouteTaskRouteResultCompat[];
}

export type RouteTaskLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface RouteTaskHandleCompat {
  remove(): void;
}

export class RouteTaskCompat {
  public readonly url: string | undefined;
  public readonly apiKey: string | undefined;
  public readonly requestOptions: Readonly<Record<string, unknown>> | undefined;
  public readonly eventBus: CompatEventBus;
  public loaded: boolean;
  public loadStatus: RouteTaskLoadStatusCompat;
  public lastSolveResult: RouteTaskSolveResultCompat | undefined;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  private readonly routeProvider: RouteLayerCompatOptions["routeProvider"] | undefined;

  public constructor(options: RouteTaskCompatOptions | string = {}) {
    if (typeof options === "string") {
      this.url = options;
      this.apiKey = undefined;
      this.requestOptions = undefined;
      this.eventBus = new CompatEventBus();
      this.loaded = false;
      this.loadStatus = "not-loaded";
      this.lastSolveResult = undefined;
      this.watchListeners = new Map();
      this.routeProvider = undefined;
      return;
    }

    this.url = options.url;
    this.apiKey = options.apiKey;
    this.requestOptions = options.requestOptions ? { ...options.requestOptions } : undefined;
    this.eventBus = options.eventBus ?? new CompatEventBus();
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.lastSolveResult = undefined;
    this.watchListeners = new Map();
    this.routeProvider = options.routeProvider;
  }

  public async load(): Promise<RouteTaskCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("route-task.loading", undefined, this);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("route-task.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (task: RouteTaskCompat) => void): Promise<RouteTaskCompat> {
    const task = await this.load();
    if (callback) {
      callback(task);
    }
    return task;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): RouteTaskHandleCompat {
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

  public async solve(params: RouteTaskSolveParametersCompat = {}): Promise<RouteTaskSolveResultCompat> {
    const stops = normalizeStops(params.stops);
    if (stops.length < 2) {
      const error = new Error("RouteTask requires at least two stops.");
      this.eventBus.emit("route-task.solve-error", { error }, this);
      throw error;
    }

    this.eventBus.emit("route-task.solve-started", { stopCount: stops.length }, this);
    try {
      const layer = new RouteLayerCompat({
        stops,
        routeProvider: this.routeProvider,
        eventBus: this.eventBus,
      });
      const route = await layer.solve();
      if (!route) {
        throw new Error("RouteTask solve produced no route result.");
      }

      const result = buildSolveResult(route, stops, params.returnDirections ?? true);
      this.lastSolveResult = result;
      this.notifyWatchers("lastSolveResult", this.lastSolveResult);
      this.eventBus.emit("route-task.solve-completed", { routeResults: result.routeResults }, this);
      return result;
    } catch (error) {
      this.lastSolveResult = undefined;
      this.notifyWatchers("lastSolveResult", this.lastSolveResult);
      this.eventBus.emit("route-task.solve-error", { error }, this);
      throw error;
    }
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

function normalizeStops(
  rawStops: RouteTaskSolveParametersCompat["stops"],
): readonly RouteStopCompat[] {
  if (!rawStops) {
    return [];
  }
  if (Array.isArray(rawStops)) {
    const arrayStops = rawStops as readonly RouteStopCompat[];
    return arrayStops.map((stop) => ({
      name: stop.name,
      location: [stop.location[0], stop.location[1]],
    }));
  }

  const featureSet = rawStops as RouteTaskStopsFeatureSetCompat;
  const features = featureSet.features ?? [];
  const stops: RouteStopCompat[] = [];
  for (const feature of features) {
    const geometry = feature.geometry;
    if (!geometry) {
      continue;
    }
    const x = geometry.x ?? geometry.longitude;
    const y = geometry.y ?? geometry.latitude;
    if (typeof x !== "number" || typeof y !== "number") {
      continue;
    }
    const name = typeof feature.attributes?.Name === "string" ? feature.attributes.Name : undefined;
    stops.push({
      name,
      location: [x, y],
    });
  }
  return stops;
}

function buildSolveResult(
  route: RouteSolveResultCompat,
  stops: readonly RouteStopCompat[],
  includeDirections: boolean,
): RouteTaskSolveResultCompat {
  const kilometers = route.totalLengthMeters / 1000;
  const miles = route.totalLengthMeters * 0.000621371;
  const totalMinutes = route.totalTimeSeconds / 60;
  const directionsFeatures = includeDirections ? buildDirectionFeatures(route) : [];

  return {
    routeResults: [
      {
        route: {
          geometry: {
            paths: [route.path.map((point) => [point[0], point[1]])],
            spatialReference: { wkid: 4326 },
          },
          attributes: {
            Total_Kilometers: kilometers,
            Total_Miles: miles,
            Total_TravelTime: totalMinutes,
            Total_Length: kilometers,
          },
        },
        directions: includeDirections
          ? {
              totalLength: kilometers,
              totalTime: totalMinutes,
              features: directionsFeatures,
            }
          : undefined,
        stops: stops.map((stop) => ({ name: stop.name, location: [stop.location[0], stop.location[1]] })),
      },
    ],
  };
}

function buildDirectionFeatures(
  route: RouteSolveResultCompat,
): readonly RouteTaskDirectionsFeatureCompat[] {
  if (route.path.length < 2 || route.totalLengthMeters <= 0) {
    return [];
  }

  const features: RouteTaskDirectionsFeatureCompat[] = [];
  for (let i = 1; i < route.path.length; i += 1) {
    const start = route.path[i - 1];
    const end = route.path[i];
    const segmentMeters = haversineDistanceMeters(start, end);
    if (segmentMeters <= 0) {
      continue;
    }
    const ratio = segmentMeters / route.totalLengthMeters;
    const segmentTimeMinutes = (route.totalTimeSeconds * ratio) / 60;
    features.push({
      attributes: {
        text: `Segment ${i}`,
        length: segmentMeters / 1000,
        time: segmentTimeMinutes,
      },
      geometry: {
        paths: [[[start[0], start[1]], [end[0], end[1]]]],
        spatialReference: { wkid: 4326 },
      },
    });
  }

  return features;
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
