import { CompatEventBus, type CompatEventSubscription, resolveCompatEventBus } from "./event-bus.js";

export interface TableListCompatOptions {
  view?: unknown;
  map?: unknown;
  container?: unknown;
  tables?: readonly unknown[];
  eventBus?: CompatEventBus;
  autoRefresh?: boolean;
}

export class TableListCompat {
  public readonly view: unknown;
  public readonly map: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public readonly autoRefresh: boolean;
  public tables: unknown[];

  private readonly subscriptions: CompatEventSubscription[];
  private explicitTables: unknown[] | undefined;

  public constructor(options: TableListCompatOptions = {}) {
    this.view = options.view;
    this.map = options.map ?? extractViewMap(options.view);
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view, options.map) ?? new CompatEventBus();
    this.autoRefresh = options.autoRefresh ?? true;
    this.explicitTables = options.tables ? [...options.tables] : undefined;
    this.tables = this.explicitTables ? [...this.explicitTables] : extractTablesFromMap(this.map);
    this.subscriptions = [];

    if (this.autoRefresh) {
      this.subscriptions.push(
        this.eventBus.on("map.tables-changed", () => {
          if (this.explicitTables === undefined) {
            this.refresh();
          }
        }),
      );
    }
  }

  public setTables(tables: readonly unknown[]): void {
    this.explicitTables = [...tables];
    this.tables = [...this.explicitTables];
    this.eventBus.emit("table-list.tables-changed", { count: this.tables.length }, this);
  }

  public useMapTables(): readonly unknown[] {
    this.explicitTables = undefined;
    return this.refresh();
  }

  public refresh(): readonly unknown[] {
    if (this.explicitTables) {
      this.tables = [...this.explicitTables];
    } else {
      this.tables = extractTablesFromMap(this.map);
    }

    this.eventBus.emit("table-list.refreshed", { count: this.tables.length }, this);
    return this.tables;
  }

  public destroy(): void {
    for (const subscription of this.subscriptions.splice(0)) {
      subscription.remove();
    }
  }
}

function extractViewMap(view: unknown): unknown {
  if (!isRecord(view)) {
    return undefined;
  }
  return view.map;
}

function extractTablesFromMap(map: unknown): unknown[] {
  if (!isRecord(map)) {
    return [];
  }
  if (Array.isArray(map.allTables)) {
    return [...map.allTables];
  }
  if (Array.isArray(map.tables)) {
    return [...map.tables];
  }
  return [];
}

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null;
}
