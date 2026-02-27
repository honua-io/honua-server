export interface PictureMarkerSymbolCompatOptions {
  url?: string;
  width?: number | string;
  height?: number | string;
  xoffset?: number;
  yoffset?: number;
  angle?: number;
  opacity?: number;
}

export class PictureMarkerSymbolCompat {
  public url: string | undefined;
  public width: number | string | undefined;
  public height: number | string | undefined;
  public xoffset: number;
  public yoffset: number;
  public angle: number;
  public opacity: number;

  public constructor(options: PictureMarkerSymbolCompatOptions = {}) {
    this.url = options.url;
    this.width = options.width;
    this.height = options.height;
    this.xoffset = finiteNumber(options.xoffset, 0);
    this.yoffset = finiteNumber(options.yoffset, 0);
    this.angle = finiteNumber(options.angle, 0);
    this.opacity = clampOpacity(options.opacity);
  }

  public clone(): PictureMarkerSymbolCompat {
    return new PictureMarkerSymbolCompat(this.toJSON());
  }

  public toJSON(): PictureMarkerSymbolCompatOptions {
    return {
      url: this.url,
      width: this.width,
      height: this.height,
      xoffset: this.xoffset,
      yoffset: this.yoffset,
      angle: this.angle,
      opacity: this.opacity,
    };
  }
}

function finiteNumber(value: unknown, fallback: number): number {
  return typeof value === "number" && Number.isFinite(value) ? value : fallback;
}

function clampOpacity(value: unknown): number {
  const numeric = finiteNumber(value, 1);
  if (numeric < 0) {
    return 0;
  }
  if (numeric > 1) {
    return 1;
  }
  return numeric;
}
