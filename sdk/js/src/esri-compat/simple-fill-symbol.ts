export interface SimpleFillSymbolCompatOptions {
  style?: string;
  color?: unknown;
  outline?: unknown;
}

export class SimpleFillSymbolCompat {
  public style: string;
  public color: unknown;
  public outline: unknown;

  public constructor(options: SimpleFillSymbolCompatOptions = {}) {
    this.style = options.style ?? "solid";
    this.color = options.color;
    this.outline = options.outline;
  }

  public clone(): SimpleFillSymbolCompat {
    return new SimpleFillSymbolCompat(this.toJSON());
  }

  public toJSON(): SimpleFillSymbolCompatOptions {
    return {
      style: this.style,
      color: this.color,
      outline: this.outline,
    };
  }
}
