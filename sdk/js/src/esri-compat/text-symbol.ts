export interface TextSymbolCompatOptions {
  text?: string;
  color?: unknown;
  haloColor?: unknown;
  haloSize?: number | string;
  font?: unknown;
  xoffset?: number;
  yoffset?: number;
  angle?: number;
}

export class TextSymbolCompat {
  public text: string;
  public color: unknown;
  public haloColor: unknown;
  public haloSize: number | string | undefined;
  public font: unknown;
  public xoffset: number;
  public yoffset: number;
  public angle: number;

  public constructor(options: TextSymbolCompatOptions = {}) {
    this.text = options.text ?? "";
    this.color = options.color;
    this.haloColor = options.haloColor;
    this.haloSize = options.haloSize;
    this.font = options.font;
    this.xoffset = finiteNumber(options.xoffset, 0);
    this.yoffset = finiteNumber(options.yoffset, 0);
    this.angle = finiteNumber(options.angle, 0);
  }

  public clone(): TextSymbolCompat {
    return new TextSymbolCompat(this.toJSON());
  }

  public toJSON(): TextSymbolCompatOptions {
    return {
      text: this.text,
      color: this.color,
      haloColor: this.haloColor,
      haloSize: this.haloSize,
      font: this.font,
      xoffset: this.xoffset,
      yoffset: this.yoffset,
      angle: this.angle,
    };
  }
}

function finiteNumber(value: unknown, fallback: number): number {
  return typeof value === "number" && Number.isFinite(value) ? value : fallback;
}
