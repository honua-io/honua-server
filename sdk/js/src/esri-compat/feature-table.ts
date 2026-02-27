import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";
import { FeatureLayerCompat } from "./feature-layer.js";
import type { QueryMethod } from "../core/types.js";

export interface FeatureTableCompatOptions {
  view?: unknown;
  layer?: FeatureLayerCompat;
  container?: unknown;
  eventBus?: CompatEventBus;
  title?: unknown;
  description?: string;
  actionColumnConfig?: unknown;
  attachmentsEnabled?: boolean;
  paginationEnabled?: boolean;
  editingEnabled?: boolean;
  multiSortEnabled?: boolean;
  relatedRecordsEnabled?: boolean;
  objectIdField?: string;
  where?: string;
  filterGeometry?: unknown;
  filterBySelectionEnabled?: boolean;
  highlightIds?: readonly number[];
  tableTemplate?: unknown;
  visibleElements?: unknown;
  fieldConfigs?: unknown;
}

export type FeatureTableStateCompat = "loading" | "loaded" | "error";
export type FeatureTableLoadStatusCompat = "not-loaded" | "loading" | "loaded" | "failed";

export interface FeatureTableHighlightIdsChangeEventCompat {
  added: number[];
  removed: number[];
}

export interface FeatureTableHighlightIdsHandleCompat {
  remove(): void;
}

export interface FeatureTableHandleCompat {
  remove(): void;
}

export interface FeatureTableQueryRelatedRecordsOptions {
  relationshipId: number;
  objectIds?: readonly number[] | string;
  where?: string;
  outFields?: string | string[];
  returnGeometry?: boolean;
  method?: QueryMethod;
  extraParams?: Record<string, string | number | boolean>;
}

export interface FeatureTableRowCompat {
  objectId: number;
  attributes: Readonly<Record<string, unknown>>;
  geometry: unknown;
}

export class FeatureTableHighlightIdsCompat {
  private readonly values: number[];
  private readonly listeners: Set<(event: FeatureTableHighlightIdsChangeEventCompat) => void>;

  public constructor(initialIds: readonly number[] = []) {
    this.values = [];
    this.listeners = new Set();
    this.set(initialIds);
  }

  public get length(): number {
    return this.values.length;
  }

  public at(index: number): number | undefined {
    return this.values.at(index);
  }

  public toArray(): number[] {
    return [...this.values];
  }

  public indexOf(objectId: number): number {
    const normalized = normalizeObjectId(objectId);
    if (normalized === undefined) {
      return -1;
    }
    return this.values.indexOf(normalized);
  }

  public add(...objectIds: readonly number[]): number {
    return this.push(...objectIds);
  }

  public push(...objectIds: readonly number[]): number {
    const additions = normalizeUniqueObjectIds(objectIds).filter(
      (objectId) => !this.values.includes(objectId),
    );
    if (additions.length === 0) {
      return this.values.length;
    }

    this.values.push(...additions);
    this.emitChange({
      added: additions,
      removed: [],
    });
    return this.values.length;
  }

  public remove(objectId: number): boolean {
    const index = this.indexOf(objectId);
    if (index < 0) {
      return false;
    }
    const removed = this.values.splice(index, 1);
    this.emitChange({
      added: [],
      removed: removed,
    });
    return true;
  }

  public removeAll(): void {
    if (this.values.length === 0) {
      return;
    }

    const removed = [...this.values];
    this.values.length = 0;
    this.emitChange({
      added: [],
      removed,
    });
  }

  public set(objectIds: readonly number[]): void {
    const next = normalizeUniqueObjectIds(objectIds);
    const removed = this.values.filter((objectId) => !next.includes(objectId));
    const added = next.filter((objectId) => !this.values.includes(objectId));
    if (removed.length === 0 && added.length === 0 && this.values.length === next.length) {
      return;
    }

    this.values.length = 0;
    this.values.push(...next);
    this.emitChange({
      added,
      removed,
    });
  }

  public splice(start: number, deleteCount?: number, ...items: readonly number[]): number[] {
    const currentLength = this.values.length;
    const normalizedStart = normalizeSpliceStart(start, currentLength);
    const normalizedDeleteCount = normalizeSpliceDeleteCount(deleteCount, normalizedStart, currentLength);
    const removed = this.values.splice(normalizedStart, normalizedDeleteCount);

    const additions = normalizeUniqueObjectIds(items).filter((objectId) => !this.values.includes(objectId));
    if (additions.length > 0) {
      this.values.splice(normalizedStart, 0, ...additions);
    }

    if (removed.length > 0 || additions.length > 0) {
      this.emitChange({
        added: additions,
        removed,
      });
    }
    return removed;
  }

  public on(
    type: "change",
    listener: (event: FeatureTableHighlightIdsChangeEventCompat) => void,
  ): FeatureTableHighlightIdsHandleCompat {
    if (type !== "change") {
      return { remove: () => undefined };
    }

    this.listeners.add(listener);
    return {
      remove: () => {
        this.listeners.delete(listener);
      },
    };
  }

  public [Symbol.iterator](): Iterator<number> {
    return this.values[Symbol.iterator]();
  }

  private emitChange(event: FeatureTableHighlightIdsChangeEventCompat): void {
    for (const listener of this.listeners) {
      try {
        listener({
          added: [...event.added],
          removed: [...event.removed],
        });
      } catch {
        // Listener errors should not break compatibility flow.
      }
    }
  }
}

export class FeatureTableCompat {
  public readonly view: unknown;
  public readonly layer: FeatureLayerCompat | undefined;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public title: unknown;
  public description: string | undefined;
  public actionColumnConfig: unknown;
  public attachmentsEnabled: boolean;
  public paginationEnabled: boolean;
  public editingEnabled: boolean;
  public multiSortEnabled: boolean;
  public relatedRecordsEnabled: boolean;
  public readonly objectIdField: string;
  public state: FeatureTableStateCompat;
  public loadStatus: FeatureTableLoadStatusCompat;
  public filterGeometry: unknown;
  public filterBySelectionEnabled: boolean;
  public where: string;
  public tableTemplate: unknown;
  public visibleElements: unknown;
  public fieldConfigs: unknown;
  public readonly highlightIds: FeatureTableHighlightIdsCompat;
  public rows: readonly FeatureTableRowCompat[];
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;
  private refreshRevision: number;

  public get size(): number {
    return this.rows.length;
  }

  public get loaded(): boolean {
    return this.loadStatus === "loaded" || this.state === "loaded";
  }

  public constructor(options: FeatureTableCompatOptions = {}) {
    this.view = options.view;
    this.layer = options.layer;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view, options.layer) ?? new CompatEventBus();
    this.title = options.title;
    this.description = options.description;
    this.actionColumnConfig = options.actionColumnConfig;
    this.attachmentsEnabled = options.attachmentsEnabled ?? false;
    this.paginationEnabled = options.paginationEnabled ?? false;
    this.editingEnabled = options.editingEnabled ?? false;
    this.multiSortEnabled = options.multiSortEnabled ?? false;
    this.relatedRecordsEnabled = options.relatedRecordsEnabled ?? false;
    this.objectIdField = options.objectIdField ?? "OBJECTID";
    this.state = "loading";
    this.loadStatus = "not-loaded";
    this.where = options.where ?? "1=1";
    this.filterGeometry = options.filterGeometry;
    this.filterBySelectionEnabled = options.filterBySelectionEnabled ?? false;
    this.tableTemplate = options.tableTemplate;
    this.visibleElements = options.visibleElements;
    this.fieldConfigs = options.fieldConfigs;
    this.highlightIds = new FeatureTableHighlightIdsCompat(options.highlightIds);
    this.rows = [];
    this.watchListeners = new Map();
    this.refreshRevision = 0;

    this.highlightIds.on("change", (event) => {
      this.notifyWatchers("highlightIds", this.highlightIds.toArray());
      this.eventBus.emit(
        "feature-table.selection-changed",
        {
          objectIds: this.highlightIds.toArray(),
          added: event.added,
          removed: event.removed,
        },
        this,
      );
    });
  }

  public async when(callback?: (table: FeatureTableCompat) => void): Promise<FeatureTableCompat> {
    await this.load();
    if (callback) {
      callback(this);
    }
    return this;
  }

  public async load(): Promise<FeatureTableCompat> {
    if (this.loadStatus === "loaded") {
      return this;
    }

    await this.refresh();
    return this;
  }
  public watch(propertyName: string, listener: (value: unknown) => void): FeatureTableHandleCompat {
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

  public async refresh(): Promise<readonly FeatureTableRowCompat[]> {
    const refreshRevision = this.nextRefreshRevision();
    if (this.loadStatus !== "loading") {
      this.loadStatus = "loading";
      this.notifyWatchers("loadStatus", this.loadStatus);
      this.eventBus.emit("feature-table.loading", undefined, this);
    }

    this.state = "loading";
    this.notifyWatchers("state", this.state);
    this.eventBus.emit("feature-table.state-changed", { state: this.state }, this);

    if (!this.layer) {
      this.rows = [];
      this.notifyWatchers("rows", this.rows);
      this.notifyWatchers("size", this.size);
      this.state = "loaded";
      this.notifyWatchers("state", this.state);
      this.eventBus.emit("feature-table.state-changed", { state: this.state }, this);
      this.loadStatus = "loaded";
      this.notifyWatchers("loadStatus", this.loadStatus);
      this.notifyWatchers("loaded", this.loaded);
      this.eventBus.emit("feature-table.loaded", { rowCount: 0 }, this);
      this.eventBus.emit("feature-table.refreshed", { rowCount: 0 }, this);
      return this.rows;
    }

    try {
      const response = await this.layer.queryFeatures({
        where: this.where,
        returnGeometry: true,
      });
      if (refreshRevision !== this.refreshRevision) {
        return this.rows;
      }
      this.rows = extractRows(response, this.objectIdField);
      this.notifyWatchers("rows", this.rows);
      this.notifyWatchers("size", this.size);
      this.state = "loaded";
      this.notifyWatchers("state", this.state);
      this.eventBus.emit("feature-table.state-changed", { state: this.state }, this);
      this.loadStatus = "loaded";
      this.notifyWatchers("loadStatus", this.loadStatus);
      this.notifyWatchers("loaded", this.loaded);
      this.eventBus.emit("feature-table.loaded", { rowCount: this.rows.length }, this);
      this.eventBus.emit("feature-table.refreshed", { rowCount: this.rows.length }, this);
      return this.rows;
    } catch (error) {
      if (refreshRevision !== this.refreshRevision) {
        return this.rows;
      }
      this.state = "error";
      this.notifyWatchers("state", this.state);
      this.eventBus.emit("feature-table.state-changed", { state: this.state }, this);
      this.loadStatus = "failed";
      this.notifyWatchers("loadStatus", this.loadStatus);
      this.notifyWatchers("loaded", this.loaded);
      this.eventBus.emit("feature-table.failed", { error }, this);
      throw error;
    }
  }

  public setWhere(where: string): void {
    this.where = where;
    this.notifyWatchers("where", this.where);
    this.eventBus.emit("feature-table.filter-changed", { where }, this);
  }

  public setFilterGeometry(filterGeometry: unknown): void {
    this.filterGeometry = filterGeometry;
    this.notifyWatchers("filterGeometry", this.filterGeometry);
    this.eventBus.emit("feature-table.filter-geometry-changed", { filterGeometry }, this);
  }

  public selectRows(objectIds: readonly number[]): void {
    this.highlightIds.set(objectIds);
  }

  public clearSelection(): void {
    this.highlightIds.removeAll();
  }

  public getSelectedObjectIds(): readonly number[] {
    return this.highlightIds.toArray();
  }

  public getSelectedRows(): readonly FeatureTableRowCompat[] {
    if (this.highlightIds.length === 0) {
      return [];
    }
    const selectedIds = new Set(this.highlightIds.toArray());
    return this.rows.filter((row) => selectedIds.has(row.objectId));
  }

  public async queryRelatedRecords(options: FeatureTableQueryRelatedRecordsOptions): Promise<unknown> {
    if (!this.layer) {
      return { relatedRecordGroups: [] };
    }

    const objectIds: string | number[] | undefined =
      options.objectIds === undefined
        ? this.highlightIds.length > 0
          ? this.highlightIds.toArray()
          : undefined
        : Array.isArray(options.objectIds)
          ? [...options.objectIds]
          : (options.objectIds as string);
    return this.layer.queryRelatedFeatures({
      relationshipId: options.relationshipId,
      objectIds,
      where: options.where ?? this.where,
      outFields: options.outFields,
      returnGeometry: options.returnGeometry,
      method: options.method,
      extraParams: options.extraParams,
    });
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

  private nextRefreshRevision(): number {
    this.refreshRevision += 1;
    return this.refreshRevision;
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

function normalizeObjectId(value: unknown): number | undefined {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : undefined;
}

function normalizeUniqueObjectIds(values: readonly unknown[]): number[] {
  const normalized: number[] = [];
  for (const value of values) {
    const objectId = normalizeObjectId(value);
    if (objectId === undefined || normalized.includes(objectId)) {
      continue;
    }
    normalized.push(objectId);
  }
  return normalized;
}

function normalizeSpliceStart(start: number, length: number): number {
  if (!Number.isFinite(start)) {
    return length;
  }
  const integer = Math.trunc(start);
  if (integer < 0) {
    return Math.max(length + integer, 0);
  }
  return Math.min(integer, length);
}

function normalizeSpliceDeleteCount(
  deleteCount: number | undefined,
  start: number,
  length: number,
): number {
  if (deleteCount === undefined) {
    return Math.max(length - start, 0);
  }
  if (!Number.isFinite(deleteCount)) {
    return 0;
  }
  return Math.min(Math.max(Math.trunc(deleteCount), 0), Math.max(length - start, 0));
}
