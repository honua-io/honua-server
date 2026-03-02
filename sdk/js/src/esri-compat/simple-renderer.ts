import { safeInvokeCompatListener } from "./event-bus.js";
export interface SimpleRendererCompatOptions {
  symbol?: unknown;
  label?: string;
  description?: string;
  visualVariables?: unknown[];
}

export type SimpleRendererLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface SimpleRendererHandleCompat {
  remove(): void;
}

export class SimpleRendererCompat {
  public loaded: boolean;
  public loadStatus: SimpleRendererLoadStatusCompat;
  public symbol: unknown;
  public label: string | undefined;
  public description: string | undefined;
  public visualVariables: unknown[];
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: SimpleRendererCompatOptions = {}) {
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.symbol = options.symbol;
    this.label = options.label;
    this.description = options.description;
    this.visualVariables = options.visualVariables ? [...options.visualVariables] : [];
    this.watchListeners = new Map();
  }

  public async load(): Promise<SimpleRendererCompat> {
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

  public async when(callback?: (renderer: SimpleRendererCompat) => void): Promise<SimpleRendererCompat> {
    const renderer = await this.load();
    if (callback) {
      callback(renderer);
    }
    return renderer;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): SimpleRendererHandleCompat {
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

  public update(options: SimpleRendererCompatOptions): void {
    if (options.symbol !== undefined) {
      this.symbol = options.symbol;
      this.notifyWatchers("symbol", this.symbol);
    }
    if (options.label !== undefined) {
      this.label = options.label;
      this.notifyWatchers("label", this.label);
    }
    if (options.description !== undefined) {
      this.description = options.description;
      this.notifyWatchers("description", this.description);
    }
    if (options.visualVariables !== undefined) {
      this.visualVariables = [...options.visualVariables];
      this.notifyWatchers("visualVariables", this.visualVariables);
    }
  }

  public clone(): SimpleRendererCompat {
    return new SimpleRendererCompat(this.toJSON());
  }

  public toJSON(): SimpleRendererCompatOptions {
    return {
      symbol: this.symbol,
      label: this.label,
      description: this.description,
      visualVariables: [...this.visualVariables],
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
