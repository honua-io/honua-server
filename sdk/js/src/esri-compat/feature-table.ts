import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";
import { FeatureLayerCompat } from "./feature-layer.js";

export interface FeatureTableCompatOptions {
  view?: unknown;
  layer?: FeatureLayerCompat;
  container?: unknown;
  eventBus?: CompatEventBus;
  objectIdField?: string;
  where?: string;
  filterGeometry?: unknown;
  tableTemplate?: unknown;
  visibleElements?: unknown;
}

export interface FeatureTableRowCompat {
  objectId: number;
  attributes: Readonly<Record<string, unknown>>;
  geometry: unknown;
}

export class FeatureTableCompat {
  public readonly view: unknown;
  public readonly layer: FeatureLayerCompat | undefined;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public readonly objectIdField: string;
  public filterGeometry: unknown;
  public where: string;
  public rows: readonly FeatureTableRowCompat[];

  private readonly selectedObjectIdsInternal: Set<number>;

  public constructor(options: FeatureTableCompatOptions = {}) {
    this.view = options.view;
    this.layer = options.layer;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view, options.layer) ?? new CompatEventBus();
    this.objectIdField = options.objectIdField ?? "OBJECTID";
    this.where = options.where ?? "1=1";
    this.filterGeometry = options.filterGeometry;
    this.rows = [];
    this.selectedObjectIdsInternal = new Set();
  }

  public async refresh(): Promise<readonly FeatureTableRowCompat[]> {
    if (!this.layer) {
      this.rows = [];
      this.eventBus.emit("feature-table.refreshed", { rowCount: 0 }, this);
      return this.rows;
    }

    const response = await this.layer.queryFeatures({
      where: this.where,
      returnGeometry: true,
    });
    this.rows = extractRows(response, this.objectIdField);
    this.eventBus.emit("feature-table.refreshed", { rowCount: this.rows.length }, this);
    return this.rows;
  }

  public setWhere(where: string): void {
    this.where = where;
    this.eventBus.emit("feature-table.filter-changed", { where }, this);
  }

  public selectRows(objectIds: readonly number[]): void {
    this.selectedObjectIdsInternal.clear();
    for (const id of objectIds) {
      const normalized = Number(id);
      if (Number.isFinite(normalized)) {
        this.selectedObjectIdsInternal.add(normalized);
      }
    }
    this.eventBus.emit("feature-table.selection-changed", { objectIds: this.getSelectedObjectIds() }, this);
  }

  public clearSelection(): void {
    this.selectedObjectIdsInternal.clear();
    this.eventBus.emit("feature-table.selection-changed", { objectIds: [] }, this);
  }

  public getSelectedObjectIds(): readonly number[] {
    return Array.from(this.selectedObjectIdsInternal.values());
  }

  public getSelectedRows(): readonly FeatureTableRowCompat[] {
    if (this.selectedObjectIdsInternal.size === 0) {
      return [];
    }
    return this.rows.filter((row) => this.selectedObjectIdsInternal.has(row.objectId));
  }
}

function extractRows(
  response: unknown,
  objectIdField: string,
): readonly FeatureTableRowCompat[] {
  if (!isRecord(response) || !Array.isArray(response.features)) {
    return [];
  }

  const rows: FeatureTableRowCompat[] = [];
  for (const feature of response.features) {
    if (!isRecord(feature) || !isRecord(feature.attributes)) {
      continue;
    }
    const objectId = extractObjectId(feature.attributes, objectIdField);
    if (objectId === undefined) {
      continue;
    }
    rows.push({
      objectId,
      attributes: { ...feature.attributes },
      geometry: feature.geometry,
    });
  }
  return rows;
}

function extractObjectId(
  attributes: Record<string, unknown>,
  objectIdField: string,
): number | undefined {
  const preferred = Number(attributes[objectIdField]);
  if (Number.isFinite(preferred)) {
    return preferred;
  }

  for (const key of ["OBJECTID", "objectid", "ObjectId", "id"]) {
    const parsed = Number(attributes[key]);
    if (Number.isFinite(parsed)) {
      return parsed;
    }
  }
  return undefined;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}
