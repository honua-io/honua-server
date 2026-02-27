export interface SpatialReferenceCompatOptions {
  wkid?: number;
  latestWkid?: number;
  wkt?: string;
  vcsWkid?: number;
  latestVcsWkid?: number;
}

export class SpatialReferenceCompat {
  public wkid: number | undefined;
  public latestWkid: number | undefined;
  public wkt: string | undefined;
  public vcsWkid: number | undefined;
  public latestVcsWkid: number | undefined;

  public constructor(options: SpatialReferenceCompatOptions = {}) {
    this.wkid = finiteNumberOrUndefined(options.wkid);
    this.latestWkid = finiteNumberOrUndefined(options.latestWkid);
    this.wkt = options.wkt;
    this.vcsWkid = finiteNumberOrUndefined(options.vcsWkid);
    this.latestVcsWkid = finiteNumberOrUndefined(options.latestVcsWkid);
  }

  public clone(): SpatialReferenceCompat {
    return new SpatialReferenceCompat(this.toJSON());
  }

  public toJSON(): SpatialReferenceCompatOptions {
    return {
      wkid: this.wkid,
      latestWkid: this.latestWkid,
      wkt: this.wkt,
      vcsWkid: this.vcsWkid,
      latestVcsWkid: this.latestVcsWkid,
    };
  }
}

function finiteNumberOrUndefined(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}
