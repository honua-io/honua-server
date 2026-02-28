import { safeInvokeCompatListener } from "./event-bus.js";
export interface QueryCompatOptions {
  where?: string;
  outFields?: string | string[];
  returnGeometry?: boolean;
  orderByFields?: string | string[];
  objectIds?: number[] | string;
  geometry?: unknown;
  spatialRelationship?: string;
  outSpatialReference?: unknown;
  num?: number;
  start?: number;
  timeExtent?: unknown;
  groupByFieldsForStatistics?: string | string[];
  outStatistics?: unknown[];
}

export type QueryLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface QueryHandleCompat {
  remove(): void;
}

export class QueryCompat {
  public loaded: boolean;
  public loadStatus: QueryLoadStatusCompat;
  public where: string;
  public outFields: string[] | undefined;
  public returnGeometry: boolean;
  public orderByFields: string[] | undefined;
  public objectIds: number[] | string | undefined;
  public geometry: unknown;
  public spatialRelationship: string | undefined;
  public outSpatialReference: unknown;
  public num: number | undefined;
  public start: number | undefined;
  public timeExtent: unknown;
  public groupByFieldsForStatistics: string[] | undefined;
  public outStatistics: unknown[] | undefined;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: QueryCompatOptions = {}) {
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.where = options.where ?? "1=1";
    this.outFields = normalizeStringList(options.outFields);
    this.returnGeometry = options.returnGeometry ?? true;
    this.orderByFields = normalizeStringList(options.orderByFields);
    this.objectIds = normalizeObjectIds(options.objectIds);
    this.geometry = options.geometry;
    this.spatialRelationship = options.spatialRelationship;
    this.outSpatialReference = options.outSpatialReference;
    this.num = normalizeFiniteNumber(options.num);
    this.start = normalizeFiniteNumber(options.start);
    this.timeExtent = options.timeExtent;
    this.groupByFieldsForStatistics = normalizeStringList(options.groupByFieldsForStatistics);
    this.outStatistics = options.outStatistics ? [...options.outStatistics] : undefined;
    this.watchListeners = new Map();
  }

  public async load(): Promise<QueryCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    return this;
  }

  public async when(callback?: (query: QueryCompat) => void): Promise<QueryCompat> {
    const query = await this.load();
    if (callback) {
      callback(query);
    }
    return query;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): QueryHandleCompat {
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

  public clone(): QueryCompat {
    return new QueryCompat(this.toJSON());
  }

  public toJSON(): QueryCompatOptions {
    return {
      where: this.where,
      outFields: this.outFields ? [...this.outFields] : undefined,
      returnGeometry: this.returnGeometry,
      orderByFields: this.orderByFields ? [...this.orderByFields] : undefined,
      objectIds: Array.isArray(this.objectIds) ? [...this.objectIds] : this.objectIds,
      geometry: this.geometry,
      spatialRelationship: this.spatialRelationship,
      outSpatialReference: this.outSpatialReference,
      num: this.num,
      start: this.start,
      timeExtent: this.timeExtent,
      groupByFieldsForStatistics: this.groupByFieldsForStatistics
        ? [...this.groupByFieldsForStatistics]
        : undefined,
      outStatistics: this.outStatistics ? [...this.outStatistics] : undefined,
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
      safeInvokeCompatListener(listener, value);
    }
  }
}

function normalizeStringList(value: string | string[] | undefined): string[] | undefined {
  if (value === undefined) {
    return undefined;
  }
  if (Array.isArray(value)) {
    return value.filter((item) => typeof item === "string");
  }
  return [value];
}

function normalizeFiniteNumber(value: number | undefined): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function normalizeObjectIds(value: number[] | string | undefined): number[] | string | undefined {
  if (value === undefined || typeof value === "string") {
    return value;
  }
  return value.filter((item) => typeof item === "number" && Number.isFinite(item));
}