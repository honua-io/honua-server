export interface PointCompatOptions {
  x?: number;
  y?: number;
  z?: number;
  m?: number;
  spatialReference?: unknown;
}

export class PointCompat {
  public x: number | undefined;
  public y: number | undefined;
  public z: number | undefined;
  public m: number | undefined;
  public spatialReference: unknown;

  public constructor(options: PointCompatOptions = {}) {
    this.x = normalizeFiniteNumber(options.x);
    this.y = normalizeFiniteNumber(options.y);
    this.z = normalizeFiniteNumber(options.z);
    this.m = normalizeFiniteNumber(options.m);
    this.spatialReference = options.spatialReference;
  }

  public clone(): PointCompat {
    return new PointCompat(this.toJSON());
  }

  public toJSON(): PointCompatOptions {
    return {
      x: this.x,
      y: this.y,
      z: this.z,
      m: this.m,
      spatialReference: this.spatialReference,
    };
  }
}

function normalizeFiniteNumber(value: number | undefined): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}
