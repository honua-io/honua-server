export interface GraphicCompatOptions {
  geometry?: unknown;
  symbol?: unknown;
  attributes?: Record<string, unknown>;
  popupTemplate?: unknown;
  layer?: unknown;
}

export class GraphicCompat {
  public geometry: unknown;
  public symbol: unknown;
  public attributes: Record<string, unknown>;
  public popupTemplate: unknown;
  public layer: unknown;

  public constructor(options: GraphicCompatOptions = {}) {
    this.geometry = options.geometry;
    this.symbol = options.symbol;
    this.attributes = options.attributes ? { ...options.attributes } : {};
    this.popupTemplate = options.popupTemplate;
    this.layer = options.layer;
  }

  public setGeometry(geometry: unknown): void {
    this.geometry = geometry;
  }

  public setSymbol(symbol: unknown): void {
    this.symbol = symbol;
  }

  public setAttributes(attributes: Record<string, unknown>): void {
    this.attributes = { ...attributes };
  }

  public clone(): GraphicCompat {
    return new GraphicCompat({
      geometry: this.geometry,
      symbol: this.symbol,
      attributes: this.attributes,
      popupTemplate: this.popupTemplate,
      layer: this.layer,
    });
  }

  public toJSON(): Record<string, unknown> {
    return {
      geometry: this.geometry,
      symbol: this.symbol,
      attributes: { ...this.attributes },
      popupTemplate: this.popupTemplate,
      layer: this.layer,
    };
  }
}
