import type { HonuaClient } from "./client.js";
import type {
  EsriGeometryType,
  EsriSpatialRel,
  HonuaQueryResponse,
  QueryFeaturesRequest,
  QueryMethod,
} from "./types.js";

/**
 * Fluent builder for constructing `QueryFeaturesRequest` objects.
 *
 * Create via `QueryBuilder.from(serviceId, layerId)` (standalone) or
 * `QueryBuilder.for(client, serviceId, layerId)` (bound to a client for `.run()`).
 *
 * @example
 * ```ts
 * // Standalone — build request object
 * const req = QueryBuilder.from("myService", 0)
 *   .where("POP > 1000")
 *   .outFields("NAME", "POP")
 *   .limit(100)
 *   .build();
 *
 * const result = await client.queryFeatures(req);
 *
 * // Bound — build and execute in one chain
 * const result = await QueryBuilder.for(client, "myService", 0)
 *   .where("POP > 1000")
 *   .outFields("NAME", "POP")
 *   .limit(100)
 *   .run();
 * ```
 */
export class QueryBuilder {
  private readonly serviceId: string;
  private readonly layerId: number;
  private readonly boundClient: HonuaClient | undefined;
  private whereClause: string | undefined;
  private fields: string[] | undefined;
  private geometryFilter: string | Record<string, unknown> | undefined;
  private geometryTypeValue: EsriGeometryType | undefined;
  private spatialRelValue: EsriSpatialRel | undefined;
  private returnGeometryValue: boolean | undefined;
  private orderByFieldsValue: string | undefined;
  private resultOffsetValue: number | undefined;
  private resultRecordCountValue: number | undefined;
  private objectIdsValue: number[] | string | undefined;
  private returnDistinctValuesValue: boolean | undefined;
  private returnCentroidValue: boolean | undefined;
  private groupByFieldsValue: string | undefined;
  private outStatisticsValue: string | readonly Record<string, unknown>[] | undefined;
  private methodValue: QueryMethod | undefined;
  private signalValue: AbortSignal | undefined;
  private extraParamsValue: Record<string, string | number | boolean> | undefined;

  private constructor(serviceId: string, layerId: number, client?: HonuaClient) {
    this.serviceId = serviceId;
    this.layerId = layerId;
    this.boundClient = client;
  }

  /** Create a standalone builder (call `.build()` to get the request object). */
  public static from(serviceId: string, layerId: number): QueryBuilder {
    return new QueryBuilder(serviceId, layerId);
  }

  /** Create a builder bound to a client (enables `.run()` to execute the query). */
  public static for(client: HonuaClient, serviceId: string, layerId: number): QueryBuilder {
    return new QueryBuilder(serviceId, layerId, client);
  }

  /** Set the WHERE clause for filtering features. */
  public where(clause: string): this {
    this.whereClause = clause;
    return this;
  }

  /** Set the output fields to return. Accepts individual field names. */
  public outFields(...fieldNames: string[]): this {
    this.fields = fieldNames;
    return this;
  }

  /** Set a geometry filter. Accepts a geometry object or JSON string. */
  public geometry(geom: string | Record<string, unknown>): this {
    this.geometryFilter = geom;
    return this;
  }

  /** Set the geometry type for the geometry filter. */
  public geometryType(type: EsriGeometryType): this {
    this.geometryTypeValue = type;
    return this;
  }

  /** Set the spatial relationship for the geometry filter. */
  public spatialRel(rel: EsriSpatialRel): this {
    this.spatialRelValue = rel;
    return this;
  }

  /** Whether to include geometry in the response. */
  public returnGeometry(value: boolean): this {
    this.returnGeometryValue = value;
    return this;
  }

  /** Set the fields to order results by (e.g. `"NAME ASC"`). */
  public orderBy(fields: string): this {
    this.orderByFieldsValue = fields;
    return this;
  }

  /** Set the maximum number of features to return. */
  public limit(count: number): this {
    this.resultRecordCountValue = count;
    return this;
  }

  /** Set the number of features to skip (for pagination). */
  public offset(count: number): this {
    this.resultOffsetValue = count;
    return this;
  }

  /** Filter by specific object IDs. */
  public objectIds(ids: number[] | string): this {
    this.objectIdsValue = ids;
    return this;
  }

  /** Return only distinct values. */
  public distinct(value = true): this {
    this.returnDistinctValuesValue = value;
    return this;
  }

  /** Return centroids instead of full geometries. */
  public returnCentroid(value = true): this {
    this.returnCentroidValue = value;
    return this;
  }

  /** Set group-by fields for statistics queries. */
  public groupBy(fields: string): this {
    this.groupByFieldsValue = fields;
    return this;
  }

  /** Set output statistics definitions. */
  public outStatistics(stats: string | readonly Record<string, unknown>[]): this {
    this.outStatisticsValue = stats;
    return this;
  }

  /** Set the HTTP method (GET or POST). */
  public method(m: QueryMethod): this {
    this.methodValue = m;
    return this;
  }

  /** Attach an AbortSignal for cancellation. */
  public signal(s: AbortSignal): this {
    this.signalValue = s;
    return this;
  }

  /** Set extra query parameters. */
  public extraParams(params: Record<string, string | number | boolean>): this {
    this.extraParamsValue = params;
    return this;
  }

  /** Build the `QueryFeaturesRequest` object without executing it. */
  public build(): QueryFeaturesRequest {
    const request: QueryFeaturesRequest = {
      serviceId: this.serviceId,
      layerId: this.layerId,
    };

    if (this.whereClause !== undefined) request.where = this.whereClause;
    if (this.fields !== undefined) request.outFields = this.fields;
    if (this.geometryFilter !== undefined) request.geometry = this.geometryFilter;
    if (this.geometryTypeValue !== undefined) request.geometryType = this.geometryTypeValue;
    if (this.spatialRelValue !== undefined) request.spatialRel = this.spatialRelValue;
    if (this.returnGeometryValue !== undefined) request.returnGeometry = this.returnGeometryValue;
    if (this.orderByFieldsValue !== undefined) request.orderByFields = this.orderByFieldsValue;
    if (this.resultOffsetValue !== undefined) request.resultOffset = this.resultOffsetValue;
    if (this.resultRecordCountValue !== undefined) request.resultRecordCount = this.resultRecordCountValue;
    if (this.objectIdsValue !== undefined) request.objectIds = this.objectIdsValue;
    if (this.returnDistinctValuesValue !== undefined) request.returnDistinctValues = this.returnDistinctValuesValue;
    if (this.returnCentroidValue !== undefined) request.returnCentroid = this.returnCentroidValue;
    if (this.groupByFieldsValue !== undefined) request.groupByFieldsForStatistics = this.groupByFieldsValue;
    if (this.outStatisticsValue !== undefined) request.outStatistics = this.outStatisticsValue;
    if (this.methodValue !== undefined) request.method = this.methodValue;
    if (this.signalValue !== undefined) request.signal = this.signalValue;
    if (this.extraParamsValue !== undefined) request.extraParams = this.extraParamsValue;

    return request;
  }

  /**
   * Build the request and execute it via the bound client.
   * Throws if the builder was created with `QueryBuilder.from()` (no client).
   */
  public async run(): Promise<HonuaQueryResponse> {
    if (!this.boundClient) {
      throw new Error(
        "QueryBuilder.run() requires a bound client. Use QueryBuilder.for(client, ...) or call .build() and pass to client.queryFeatures().",
      );
    }
    return this.boundClient.queryFeatures(this.build());
  }
}
