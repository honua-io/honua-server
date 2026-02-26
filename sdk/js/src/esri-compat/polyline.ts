export interface PolylineCompatOptions {
  paths?: unknown[][][];
  spatialReference?: unknown;
  hasZ?: boolean;
  hasM?: boolean;
}

export class PolylineCompat {
  public paths: unknown[][][];
  public spatialReference: unknown;
  public hasZ: boolean;
  public hasM: boolean;

  public constructor(options: PolylineCompatOptions = {}) {
    this.paths = options.paths ? options.paths.map(clonePath) : [];
    this.spatialReference = options.spatialReference;
    this.hasZ = options.hasZ ?? false;
    this.hasM = options.hasM ?? false;
  }

  public addPath(path: unknown[][]): void {
    this.paths.push(clonePath(path));
  }

  public removePath(index: number): boolean {
    if (!Number.isInteger(index) || index < 0 || index >= this.paths.length) {
      return false;
    }
    this.paths.splice(index, 1);
    return true;
  }

  public clone(): PolylineCompat {
    return new PolylineCompat(this.toJSON());
  }

  public toJSON(): PolylineCompatOptions {
    return {
      paths: this.paths.map(clonePath),
      spatialReference: this.spatialReference,
      hasZ: this.hasZ,
      hasM: this.hasM,
    };
  }
}

function clonePath(path: readonly unknown[][]): unknown[][] {
  return path.map((point) => [...point]);
}
