export interface SimpleLineSymbolCompatOptions {
  style?: string;
  color?: unknown;
  width?: number;
}

export class SimpleLineSymbolCompat {
  public style: string;
  public color: unknown;
  public width: number;

  public constructor(options: SimpleLineSymbolCompatOptions = {}) {
    this.style = options.style ?? "solid";
    this.color = options.color;
    this.width =
      typeof options.width === "number" && Number.isFinite(options.width)
        ? options.width
        : 1;
  }

  public clone(): SimpleLineSymbolCompat {
    return new SimpleLineSymbolCompat(this.toJSON());
  }

  public toJSON(): SimpleLineSymbolCompatOptions {
    return {
      style: this.style,
      color: this.color,
      width: this.width,
    };
  }
}
