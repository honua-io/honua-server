export interface UniqueValueInfoCompat {
  value: string | number;
  symbol?: unknown;
  label?: string;
  description?: string;
}

export interface UniqueValueRendererCompatOptions {
  field?: string;
  field2?: string;
  field3?: string;
  defaultSymbol?: unknown;
  defaultLabel?: string;
  uniqueValueInfos?: UniqueValueInfoCompat[];
}

export type UniqueValueRendererLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface UniqueValueRendererHandleCompat {
  remove(): void;
}

export class UniqueValueRendererCompat {
  public loaded: boolean;
  public loadStatus: UniqueValueRendererLoadStatusCompat;
  public field: string | undefined;
  public field2: string | undefined;
  public field3: string | undefined;
  public defaultSymbol: unknown;
  public defaultLabel: string | undefined;
  public uniqueValueInfos: UniqueValueInfoCompat[];
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: UniqueValueRendererCompatOptions = {}) {
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.field = options.field;
    this.field2 = options.field2;
    this.field3 = options.field3;
    this.defaultSymbol = options.defaultSymbol;
    this.defaultLabel = options.defaultLabel;
    this.uniqueValueInfos = options.uniqueValueInfos ? options.uniqueValueInfos.map(cloneUniqueValueInfo) : [];
    this.watchListeners = new Map();
  }

  public async load(): Promise<UniqueValueRendererCompat> {
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

  public async when(callback?: (renderer: UniqueValueRendererCompat) => void): Promise<UniqueValueRendererCompat> {
    const renderer = await this.load();
    if (callback) {
      callback(renderer);
    }
    return renderer;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): UniqueValueRendererHandleCompat {
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

  public update(options: UniqueValueRendererCompatOptions): void {
    if (options.field !== undefined) {
      this.field = options.field;
      this.notifyWatchers("field", this.field);
    }
    if (options.field2 !== undefined) {
      this.field2 = options.field2;
      this.notifyWatchers("field2", this.field2);
    }
    if (options.field3 !== undefined) {
      this.field3 = options.field3;
      this.notifyWatchers("field3", this.field3);
    }
    if (options.defaultSymbol !== undefined) {
      this.defaultSymbol = options.defaultSymbol;
      this.notifyWatchers("defaultSymbol", this.defaultSymbol);
    }
    if (options.defaultLabel !== undefined) {
      this.defaultLabel = options.defaultLabel;
      this.notifyWatchers("defaultLabel", this.defaultLabel);
    }
    if (options.uniqueValueInfos !== undefined) {
      this.uniqueValueInfos = options.uniqueValueInfos.map(cloneUniqueValueInfo);
      this.notifyWatchers("uniqueValueInfos", this.uniqueValueInfos);
    }
  }

  public addUniqueValueInfo(info: UniqueValueInfoCompat): void {
    this.uniqueValueInfos.push(cloneUniqueValueInfo(info));
    this.notifyWatchers("uniqueValueInfos", this.uniqueValueInfos);
  }

  public removeUniqueValueInfo(value: string | number): boolean {
    const index = this.uniqueValueInfos.findIndex((item) => item.value === value);
    if (index < 0) {
      return false;
    }
    this.uniqueValueInfos.splice(index, 1);
    this.notifyWatchers("uniqueValueInfos", this.uniqueValueInfos);
    return true;
  }

  public clone(): UniqueValueRendererCompat {
    return new UniqueValueRendererCompat(this.toJSON());
  }

  public toJSON(): UniqueValueRendererCompatOptions {
    return {
      field: this.field,
      field2: this.field2,
      field3: this.field3,
      defaultSymbol: this.defaultSymbol,
      defaultLabel: this.defaultLabel,
      uniqueValueInfos: this.uniqueValueInfos.map(cloneUniqueValueInfo),
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
      listener(value);
    }
  }
}

function cloneUniqueValueInfo(info: UniqueValueInfoCompat): UniqueValueInfoCompat {
  return {
    value: info.value,
    symbol: info.symbol,
    label: info.label,
    description: info.description,
  };
}
