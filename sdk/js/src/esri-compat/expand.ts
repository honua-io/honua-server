import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";

export interface ExpandCompatOptions {
  view?: unknown;
  container?: unknown;
  content?: unknown;
  expanded?: boolean;
  mode?: "auto" | "floating" | "drawer";
  group?: string;
  eventBus?: CompatEventBus;
}

export type ExpandLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface ExpandHandleCompat {
  remove(): void;
}

export class ExpandCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public loaded: boolean;
  public loadStatus: ExpandLoadStatusCompat;
  public content: unknown;
  public expanded: boolean;
  public mode: "auto" | "floating" | "drawer";
  public group: string | undefined;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: ExpandCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.content = options.content;
    this.expanded = options.expanded ?? false;
    this.mode = options.mode ?? "auto";
    this.group = options.group;
    this.watchListeners = new Map();
  }

  public async load(): Promise<ExpandCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("expand.loading", undefined, this);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("expand.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (widget: ExpandCompat) => void): Promise<ExpandCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): ExpandHandleCompat {
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

  public expand(): void {
    if (this.expanded) {
      return;
    }
    this.expanded = true;
    this.notifyWatchers("expanded", this.expanded);
    this.eventBus.emit("expand.changed", { expanded: true }, this);
  }

  public collapse(): void {
    if (!this.expanded) {
      return;
    }
    this.expanded = false;
    this.notifyWatchers("expanded", this.expanded);
    this.eventBus.emit("expand.changed", { expanded: false }, this);
  }

  public toggle(force?: boolean): boolean {
    const next = force ?? !this.expanded;
    if (next) {
      this.expand();
    } else {
      this.collapse();
    }
    return this.expanded;
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
