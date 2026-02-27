export interface FeatureSetCompatOptions {
  features?: unknown[];
  fields?: unknown[];
  geometryType?: string;
  spatialReference?: unknown;
  objectIdFieldName?: string;
  displayFieldName?: string;
}

export class FeatureSetCompat {
  public features: unknown[];
  public fields: unknown[];
  public geometryType: string | undefined;
  public spatialReference: unknown;
  public objectIdFieldName: string | undefined;
  public displayFieldName: string | undefined;

  public constructor(options: FeatureSetCompatOptions = {}) {
    this.features = options.features ? [...options.features] : [];
    this.fields = options.fields ? [...options.fields] : [];
    this.geometryType = options.geometryType;
    this.spatialReference = options.spatialReference;
    this.objectIdFieldName = options.objectIdFieldName;
    this.displayFieldName = options.displayFieldName;
  }

  public clone(): FeatureSetCompat {
    return new FeatureSetCompat(this.toJSON());
  }

  public toJSON(): FeatureSetCompatOptions {
    return {
      features: [...this.features],
      fields: [...this.fields],
      geometryType: this.geometryType,
      spatialReference: this.spatialReference,
      objectIdFieldName: this.objectIdFieldName,
      displayFieldName: this.displayFieldName,
    };
  }
}
