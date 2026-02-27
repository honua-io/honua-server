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

export class ClassBreaksRendererCompat {
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

  public constructor(options: ClassBreaksRendererCompatOptions = {}) {
    this.field = options.field;
    this.normalizationField = options.normalizationField;
    this.normalizationTotal =
      typeof options.normalizationTotal === "number" && Number.isFinite(options.normalizationTotal)
        ? options.normalizationTotal
        : undefined;
    this.minValue =
      typeof options.minValue === "number" && Number.isFinite(options.minValue)
        ? options.minValue
        : undefined;
    this.defaultSymbol = options.defaultSymbol;
    this.defaultLabel = options.defaultLabel;
    this.legendOptions = options.legendOptions;
    this.valueExpression = options.valueExpression;
    this.valueExpressionTitle = options.valueExpressionTitle;
    this.classBreakInfos = options.classBreakInfos ? options.classBreakInfos.map(cloneClassBreakInfo) : [];
  }

  public addClassBreakInfo(info: ClassBreakInfoCompat): void {
    this.classBreakInfos.push(cloneClassBreakInfo(info));
  }

  public removeClassBreakInfo(maxValue: number): boolean {
    const index = this.classBreakInfos.findIndex((item) => item.maxValue === maxValue);
    if (index < 0) {
      return false;
    }

    this.classBreakInfos.splice(index, 1);
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
