export interface ExtentCompatOptions {
  xmin?: number;
  ymin?: number;
  xmax?: number;
  ymax?: number;
  zmin?: number;
  zmax?: number;
  mmin?: number;
  mmax?: number;
  spatialReference?: unknown;
}

export class ExtentCompat {
  public xmin: number;
  public ymin: number;
  public xmax: number;
  public ymax: number;
  public zmin: number | undefined;
  public zmax: number | undefined;
  public mmin: number | undefined;
  public mmax: number | undefined;
  public spatialReference: unknown;

  public constructor(options: ExtentCompatOptions = {}) {
    this.xmin = finiteNumber(options.xmin, 0);
    this.ymin = finiteNumber(options.ymin, 0);
    this.xmax = finiteNumber(options.xmax, 0);
    this.ymax = finiteNumber(options.ymax, 0);
    this.zmin = finiteNumberOrUndefined(options.zmin);
    this.zmax = finiteNumberOrUndefined(options.zmax);
    this.mmin = finiteNumberOrUndefined(options.mmin);
    this.mmax = finiteNumberOrUndefined(options.mmax);
    this.spatialReference = options.spatialReference;
  }

  public get center(): { x: number; y: number } {
    return {
      x: (this.xmin + this.xmax) / 2,
      y: (this.ymin + this.ymax) / 2,
    };
  }

  public clone(): ExtentCompat {
    return new ExtentCompat(this.toJSON());
  }

  public toJSON(): ExtentCompatOptions {
    return {
      xmin: this.xmin,
      ymin: this.ymin,
      xmax: this.xmax,
      ymax: this.ymax,
      zmin: this.zmin,
      zmax: this.zmax,
      mmin: this.mmin,
      mmax: this.mmax,
      spatialReference: this.spatialReference,
    };
  }
}

function finiteNumber(value: unknown, fallback: number): number {
  return typeof value === "number" && Number.isFinite(value) ? value : fallback;
}

function finiteNumberOrUndefined(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}
