import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";

export interface TableListCompatOptions {
  view?: unknown;
  map?: unknown;
  container?: unknown;
  tables?: readonly unknown[];
  eventBus?: CompatEventBus;
}

export class TableListCompat {
  public readonly view: unknown;
  public readonly map: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public tables: unknown[];

  public constructor(options: TableListCompatOptions = {}) {
    this.view = options.view;
    this.map = options.map;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view, options.map) ?? new CompatEventBus();
    this.tables = options.tables ? [...options.tables] : [];
  }

  public setTables(tables: readonly unknown[]): void {
    this.tables = [...tables];
    this.eventBus.emit("table-list.tables-changed", { count: this.tables.length }, this);
  }
}
