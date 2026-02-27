export interface LabelClassCompatOptions {
  labelExpressionInfo?: unknown;
  symbol?: unknown;
  where?: string;
  minScale?: number;
  maxScale?: number;
}

export class LabelClassCompat {
  public labelExpressionInfo: unknown;
  public symbol: unknown;
  public where: string | undefined;
  public minScale: number | undefined;
  public maxScale: number | undefined;

  public constructor(options: LabelClassCompatOptions = {}) {
    this.labelExpressionInfo = options.labelExpressionInfo;
    this.symbol = options.symbol;
    this.where = options.where;
    this.minScale = finiteNumberOrUndefined(options.minScale);
    this.maxScale = finiteNumberOrUndefined(options.maxScale);
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
}

function finiteNumberOrUndefined(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}
