export interface SimpleMarkerSymbolCompatOptions {
  style?: string;
  color?: unknown;
  size?: number;
  outline?: unknown;
}

export class SimpleMarkerSymbolCompat {
  public style: string;
  public color: unknown;
  public size: number;
  public outline: unknown;

  public constructor(options: SimpleMarkerSymbolCompatOptions = {}) {
    this.style = options.style ?? "circle";
    this.color = options.color;
    this.size =
      typeof options.size === "number" && Number.isFinite(options.size)
        ? options.size
        : 8;
    this.outline = options.outline;
  }

  public clone(): SimpleMarkerSymbolCompat {
    return new SimpleMarkerSymbolCompat(this.toJSON());
  }

  public toJSON(): SimpleMarkerSymbolCompatOptions {
    return {
      style: this.style,
      color: this.color,
      size: this.size,
      outline: this.outline,
    };
  }
}
