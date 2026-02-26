export interface SimpleRendererCompatOptions {
  symbol?: unknown;
  label?: string;
  description?: string;
  visualVariables?: unknown[];
}

export class SimpleRendererCompat {
  public symbol: unknown;
  public label: string | undefined;
  public description: string | undefined;
  public visualVariables: unknown[];

  public constructor(options: SimpleRendererCompatOptions = {}) {
    this.symbol = options.symbol;
    this.label = options.label;
    this.description = options.description;
    this.visualVariables = options.visualVariables ? [...options.visualVariables] : [];
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
}
