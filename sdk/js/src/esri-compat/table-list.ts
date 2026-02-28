import {
  CompatEventBus,
  type CompatEventSubscription,
  resolveCompatEventBus,
  safeInvokeCompatListener,
} from "./event-bus.js";

export interface TableListCompatOptions {
  view?: unknown;
  map?: unknown;
  container?: unknown;
  tables?: readonly unknown[];
  eventBus?: CompatEventBus;
  autoRefresh?: boolean;
}

export type TableListLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface TableListHandleCompat {
  remove(): void;
}

export class TableListCompat {
  public readonly view: unknown;
  public readonly map: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public readonly autoRefresh: boolean;
  public loaded: boolean;
  public loadStatus: TableListLoadStatusCompat;
  public tables: unknown[];

  private readonly subscriptions: CompatEventSubscription[];
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;
  private explicitTables: unknown[] | undefined;

  public constructor(options: TableListCompatOptions = {}) {
    this.view = options.view;
    this.map = options.map ?? extractViewMap(options.view);
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view, options.map) ?? new CompatEventBus();
    this.autoRefresh = options.autoRefresh ?? true;
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.explicitTables = options.tables ? [...options.tables] : undefined;
    this.tables = this.explicitTables ? [...this.explicitTables] : extractTablesFromMap(this.map);
    this.subscriptions = [];
    this.watchListeners = new Map();

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

  public async load(): Promise<TableListCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("table-list.loading", undefined, this);
    this.refresh();
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("table-list.loaded", { count: this.tables.length }, this);
    return this;
  }

  public async when(callback?: (widget: TableListCompat) => void): Promise<TableListCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): TableListHandleCompat {
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

  public setTables(tables: readonly unknown[]): void {
    this.explicitTables = [...tables];
    this.tables = [...this.explicitTables];
    this.notifyWatchers("tables", this.tables);
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

    this.notifyWatchers("tables", this.tables);
    this.eventBus.emit("table-list.refreshed", { count: this.tables.length }, this);
    return this.tables;
  }

  public destroy(): void {
    for (const subscription of this.subscriptions.splice(0)) {
      subscription.remove();
    }
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
