import { safeInvokeCompatListener } from "./event-bus.js";
export interface ClassBreakInfoCompat {
  maxValue: number;
  minValue?: number;
  symbol?: unknown;
  label?: string;
  description?: string;
}

export interface ClassBreaksRendererCompatOptions {
  field?: string;
  normalizationField?: string;
  normalizationTotal?: number;
  minValue?: number;
  defaultSymbol?: unknown;
  defaultLabel?: string;
  legendOptions?: unknown;
  valueExpression?: string;
  valueExpressionTitle?: string;
  classBreakInfos?: ClassBreakInfoCompat[];
}

export type ClassBreaksRendererLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface ClassBreaksRendererHandleCompat {
  remove(): void;
}

export class ClassBreaksRendererCompat {
  public loaded: boolean;
  public loadStatus: ClassBreaksRendererLoadStatusCompat;
  public field: string | undefined;
  public normalizationField: string | undefined;
  public normalizationTotal: number | undefined;
  public minValue: number | undefined;
  public defaultSymbol: unknown;
  public defaultLabel: string | undefined;
  public legendOptions: unknown;
  public valueExpression: string | undefined;
  public valueExpressionTitle: string | undefined;
  public classBreakInfos: ClassBreakInfoCompat[];
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: ClassBreaksRendererCompatOptions = {}) {
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.field = options.field;
    this.normalizationField = options.normalizationField;
    this.normalizationTotal =
      typeof options.normalizationTotal === "number" && Number.isFinite(options.normalizationTotal)
        ? options.normalizationTotal
        : undefined;
    this.minValue =
      typeof options.minValue === "number" && Number.isFinite(options.minValue) ? options.minValue : undefined;
    this.defaultSymbol = options.defaultSymbol;
    this.defaultLabel = options.defaultLabel;
    this.legendOptions = options.legendOptions;
    this.valueExpression = options.valueExpression;
    this.valueExpressionTitle = options.valueExpressionTitle;
    this.classBreakInfos = options.classBreakInfos ? options.classBreakInfos.map(cloneClassBreakInfo) : [];
    this.watchListeners = new Map();
  }

  public async load(): Promise<ClassBreaksRendererCompat> {
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

  public async when(callback?: (renderer: ClassBreaksRendererCompat) => void): Promise<ClassBreaksRendererCompat> {
    const renderer = await this.load();
    if (callback) {
      callback(renderer);
    }
    return renderer;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): ClassBreaksRendererHandleCompat {
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

  public update(options: ClassBreaksRendererCompatOptions): void {
    if (options.field !== undefined) {
      this.field = options.field;
      this.notifyWatchers("field", this.field);
    }
    if (options.normalizationField !== undefined) {
      this.normalizationField = options.normalizationField;
      this.notifyWatchers("normalizationField", this.normalizationField);
    }
    if (options.normalizationTotal !== undefined) {
      this.normalizationTotal =
        typeof options.normalizationTotal === "number" && Number.isFinite(options.normalizationTotal)
          ? options.normalizationTotal
          : undefined;
      this.notifyWatchers("normalizationTotal", this.normalizationTotal);
    }
    if (options.minValue !== undefined) {
      this.minValue =
        typeof options.minValue === "number" && Number.isFinite(options.minValue) ? options.minValue : undefined;
      this.notifyWatchers("minValue", this.minValue);
    }
    if (options.defaultSymbol !== undefined) {
      this.defaultSymbol = options.defaultSymbol;
      this.notifyWatchers("defaultSymbol", this.defaultSymbol);
    }
    if (options.defaultLabel !== undefined) {
      this.defaultLabel = options.defaultLabel;
      this.notifyWatchers("defaultLabel", this.defaultLabel);
    }
    if (options.legendOptions !== undefined) {
      this.legendOptions = options.legendOptions;
      this.notifyWatchers("legendOptions", this.legendOptions);
    }
    if (options.valueExpression !== undefined) {
      this.valueExpression = options.valueExpression;
      this.notifyWatchers("valueExpression", this.valueExpression);
    }
    if (options.valueExpressionTitle !== undefined) {
      this.valueExpressionTitle = options.valueExpressionTitle;
      this.notifyWatchers("valueExpressionTitle", this.valueExpressionTitle);
    }
    if (options.classBreakInfos !== undefined) {
      this.classBreakInfos = options.classBreakInfos.map(cloneClassBreakInfo);
      this.notifyWatchers("classBreakInfos", this.classBreakInfos);
    }
  }

  public addClassBreakInfo(info: ClassBreakInfoCompat): void {
    this.classBreakInfos.push(cloneClassBreakInfo(info));
    this.notifyWatchers("classBreakInfos", this.classBreakInfos);
  }

  public removeClassBreakInfo(maxValue: number): boolean {
    const index = this.classBreakInfos.findIndex((item) => item.maxValue === maxValue);
    if (index < 0) {
      return false;
    }

    this.classBreakInfos.splice(index, 1);
    this.notifyWatchers("classBreakInfos", this.classBreakInfos);
    return true;
  }

  public clone(): ClassBreaksRendererCompat {
    return new ClassBreaksRendererCompat(this.toJSON());
  }

  public toJSON(): ClassBreaksRendererCompatOptions {
    return {
      field: this.field,
      normalizationField: this.normalizationField,
      normalizationTotal: this.normalizationTotal,
      minValue: this.minValue,
      defaultSymbol: this.defaultSymbol,
      defaultLabel: this.defaultLabel,
      legendOptions: this.legendOptions,
      valueExpression: this.valueExpression,
      valueExpressionTitle: this.valueExpressionTitle,
      classBreakInfos: this.classBreakInfos.map(cloneClassBreakInfo),
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

function cloneClassBreakInfo(info: ClassBreakInfoCompat): ClassBreakInfoCompat {
  return {
    maxValue: info.maxValue,
    minValue: info.minValue,
    symbol: info.symbol,
    label: info.label,
    description: info.description,
  };
}
