export interface PolygonCompatOptions {
  rings?: unknown[][][];
  spatialReference?: unknown;
  hasZ?: boolean;
  hasM?: boolean;
}

export class PolygonCompat {
  public rings: unknown[][][];
  public spatialReference: unknown;
  public hasZ: boolean;
  public hasM: boolean;

  public constructor(options: PolygonCompatOptions = {}) {
    this.rings = options.rings ? options.rings.map(cloneRing) : [];
    this.spatialReference = options.spatialReference;
    this.hasZ = options.hasZ ?? false;
    this.hasM = options.hasM ?? false;
  }

  public addRing(ring: unknown[][]): void {
    this.rings.push(cloneRing(ring));
  }

  public removeRing(index: number): boolean {
    if (!Number.isInteger(index) || index < 0 || index >= this.rings.length) {
      return false;
    }
    this.rings.splice(index, 1);
    return true;
  }

  public clone(): PolygonCompat {
    return new PolygonCompat(this.toJSON());
  }

  public toJSON(): PolygonCompatOptions {
    return {
      rings: this.rings.map(cloneRing),
      spatialReference: this.spatialReference,
      hasZ: this.hasZ,
      hasM: this.hasM,
    };
  }
}

function cloneRing(ring: readonly unknown[][]): unknown[][] {
  return ring.map((point) => [...point]);
}
