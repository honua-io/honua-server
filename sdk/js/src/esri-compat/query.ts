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

export class QueryCompat {
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

  public constructor(options: QueryCompatOptions = {}) {
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
