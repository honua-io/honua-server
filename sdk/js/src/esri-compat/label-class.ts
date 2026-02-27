export interface LabelClassCompatOptions {
  labelExpressionInfo?: unknown;
  symbol?: unknown;
  where?: string;
  minScale?: number;
  maxScale?: number;
}

export type LabelClassLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface LabelClassHandleCompat {
  remove(): void;
}

export class LabelClassCompat {
  public loaded: boolean;
  public loadStatus: LabelClassLoadStatusCompat;
  public labelExpressionInfo: unknown;
  public symbol: unknown;
  public where: string | undefined;
  public minScale: number | undefined;
  public maxScale: number | undefined;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: LabelClassCompatOptions = {}) {
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.labelExpressionInfo = options.labelExpressionInfo;
    this.symbol = options.symbol;
    this.where = options.where;
    this.minScale = finiteNumberOrUndefined(options.minScale);
    this.maxScale = finiteNumberOrUndefined(options.maxScale);
    this.watchListeners = new Map();
  }

  public async load(): Promise<LabelClassCompat> {
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

  public async when(callback?: (labelClass: LabelClassCompat) => void): Promise<LabelClassCompat> {
    const labelClass = await this.load();
    if (callback) {
      callback(labelClass);
    }
    return labelClass;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): LabelClassHandleCompat {
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

  public update(options: LabelClassCompatOptions): void {
    if (options.labelExpressionInfo !== undefined) {
      this.labelExpressionInfo = options.labelExpressionInfo;
      this.notifyWatchers("labelExpressionInfo", this.labelExpressionInfo);
    }
    if (options.symbol !== undefined) {
      this.symbol = options.symbol;
      this.notifyWatchers("symbol", this.symbol);
    }
    if (options.where !== undefined) {
      this.where = options.where;
      this.notifyWatchers("where", this.where);
    }
    if (options.minScale !== undefined) {
      this.minScale = finiteNumberOrUndefined(options.minScale);
      this.notifyWatchers("minScale", this.minScale);
    }
    if (options.maxScale !== undefined) {
      this.maxScale = finiteNumberOrUndefined(options.maxScale);
      this.notifyWatchers("maxScale", this.maxScale);
    }
  }

  public clone(): LabelClassCompat {
    return new LabelClassCompat(this.toJSON());
  }

  public toJSON(): LabelClassCompatOptions {
    return {
      labelExpressionInfo: this.labelExpressionInfo,
      symbol: this.symbol,
      where: this.where,
      minScale: this.minScale,
      maxScale: this.maxScale,
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

function finiteNumberOrUndefined(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}
