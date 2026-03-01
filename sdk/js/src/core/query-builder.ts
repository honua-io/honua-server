import type { HonuaClient } from "./client.js";
import type {
  EsriGeometryType,
  EsriSpatialRel,
  HonuaOgcFeatureCollectionResponse,
  HonuaQueryResponse,
  MapLayerQueryRequest,
  OgcItemsRequest,
  OgcResponseFormat,
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

/**
 * Fluent builder for constructing `MapLayerQueryRequest` objects.
 *
 * Create via `MapLayerQueryBuilder.from(serviceId, layerId)` (standalone) or
 * `MapLayerQueryBuilder.for(client, serviceId, layerId)` (bound to a client for `.run()`).
 *
 * @example
 * ```ts
 * // Standalone — build request object
 * const req = MapLayerQueryBuilder.from("myService", 0)
 *   .where("POP > 1000")
 *   .outFields("NAME", "POP")
 *   .limit(100)
 *   .build();
 *
 * const result = await client.queryMapLayer(req);
 *
 * // Bound — build and execute in one chain
 * const result = await MapLayerQueryBuilder.for(client, "myService", 0)
 *   .where("POP > 1000")
 *   .outFields("NAME", "POP")
 *   .limit(100)
 *   .run();
 * ```
 */
export class MapLayerQueryBuilder {
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
  public static from(serviceId: string, layerId: number): MapLayerQueryBuilder {
    return new MapLayerQueryBuilder(serviceId, layerId);
  }

  /** Create a builder bound to a client (enables `.run()` to execute the query). */
  public static for(client: HonuaClient, serviceId: string, layerId: number): MapLayerQueryBuilder {
    return new MapLayerQueryBuilder(serviceId, layerId, client);
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

  /** Build the `MapLayerQueryRequest` object without executing it. */
  public build(): MapLayerQueryRequest {
    const request: MapLayerQueryRequest = {
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
   * Throws if the builder was created with `MapLayerQueryBuilder.from()` (no client).
   */
  public async run(): Promise<HonuaQueryResponse> {
    if (!this.boundClient) {
      throw new Error(
        "MapLayerQueryBuilder.run() requires a bound client. Use MapLayerQueryBuilder.for(client, ...) or call .build() and pass to client.queryMapLayer().",
      );
    }
    return this.boundClient.queryMapLayer(this.build());
  }
}

/**
 * Fluent builder for constructing `OgcItemsRequest` objects.
 *
 * Create via `OgcQueryBuilder.from(collectionId)` (standalone) or
 * `OgcQueryBuilder.for(client, collectionId)` (bound to a client for `.run()`).
 *
 * @example
 * ```ts
 * // Standalone — build request object
 * const req = OgcQueryBuilder.from("rivers")
 *   .limit(50)
 *   .bbox("-180,-90,180,90")
 *   .build();
 *
 * const result = await client.listOgcItems(req);
 *
 * // Bound — build and execute in one chain
 * const result = await OgcQueryBuilder.for(client, "rivers")
 *   .limit(50)
 *   .bbox("-180,-90,180,90")
 *   .run();
 * ```
 */
export class OgcQueryBuilder {
  private readonly collectionId: string | number;
  private readonly boundClient: HonuaClient | undefined;
  private limitValue: number | undefined;
  private offsetValue: number | undefined;
  private bboxValue: string | undefined;
  private datetimeValue: string | undefined;
  private filterValue: string | undefined;
  private idsValue: string | readonly (string | number)[] | undefined;
  private propertiesValue: string | readonly string[] | undefined;
  private sortbyValue: string | undefined;
  private crsValue: string | undefined;
  private signalValue: AbortSignal | undefined;
  private responseFormatValue: OgcResponseFormat | string | undefined;

  private constructor(collectionId: string | number, client?: HonuaClient) {
    this.collectionId = collectionId;
    this.boundClient = client;
  }

  /** Create a standalone builder (call `.build()` to get the request object). */
  public static from(collectionId: string | number): OgcQueryBuilder {
    return new OgcQueryBuilder(collectionId);
  }

  /** Create a builder bound to a client (enables `.run()` to execute the query). */
  public static for(client: HonuaClient, collectionId: string | number): OgcQueryBuilder {
    return new OgcQueryBuilder(collectionId, client);
  }

  /** Set the maximum number of items to return. */
  public limit(n: number): this {
    this.limitValue = n;
    return this;
  }

  /** Set the number of items to skip (for pagination). */
  public offset(n: number): this {
    this.offsetValue = n;
    return this;
  }

  /** Set the bounding box filter (e.g. `"-180,-90,180,90"`). */
  public bbox(value: string): this {
    this.bboxValue = value;
    return this;
  }

  /** Set the datetime filter (e.g. `"2020-01-01T00:00:00Z/.."`). */
  public datetime(value: string): this {
    this.datetimeValue = value;
    return this;
  }

  /** Set the CQL2 filter expression. */
  public filter(value: string): this {
    this.filterValue = value;
    return this;
  }

  /** Filter by specific feature IDs. */
  public ids(...idValues: (string | number)[]): this {
    this.idsValue = idValues;
    return this;
  }

  /** Set the properties to return. */
  public properties(...props: string[]): this {
    this.propertiesValue = props;
    return this;
  }

  /** Set the sort order (e.g. `"+name"` or `"-pop"`). */
  public sortby(value: string): this {
    this.sortbyValue = value;
    return this;
  }

  /** Set the coordinate reference system URI. */
  public crs(value: string): this {
    this.crsValue = value;
    return this;
  }

  /** Attach an AbortSignal for cancellation. */
  public signal(s: AbortSignal): this {
    this.signalValue = s;
    return this;
  }

  /** Set the response format (e.g. `"json"`, `"geojson"`). */
  public responseFormat(fmt: OgcResponseFormat | string): this {
    this.responseFormatValue = fmt;
    return this;
  }

  /** Build the `OgcItemsRequest` object without executing it. */
  public build(): OgcItemsRequest {
    const request: OgcItemsRequest = {
      collectionId: this.collectionId,
    };

    if (this.limitValue !== undefined) request.limit = this.limitValue;
    if (this.offsetValue !== undefined) request.offset = this.offsetValue;
    if (this.bboxValue !== undefined) request.bbox = this.bboxValue;
    if (this.datetimeValue !== undefined) request.datetime = this.datetimeValue;
    if (this.filterValue !== undefined) request.filter = this.filterValue;
    if (this.idsValue !== undefined) request.ids = this.idsValue;
    if (this.propertiesValue !== undefined) request.properties = this.propertiesValue;
    if (this.sortbyValue !== undefined) request.sortby = this.sortbyValue;
    if (this.crsValue !== undefined) request.crs = this.crsValue;
    if (this.signalValue !== undefined) request.signal = this.signalValue;
    if (this.responseFormatValue !== undefined) request.responseFormat = this.responseFormatValue;

    return request;
  }

  /**
   * Build the request and execute it via the bound client.
   * Throws if the builder was created with `OgcQueryBuilder.from()` (no client).
   */
  public async run(): Promise<HonuaOgcFeatureCollectionResponse> {
    if (!this.boundClient) {
      throw new Error(
        "OgcQueryBuilder.run() requires a bound client. Use OgcQueryBuilder.for(client, ...) or call .build() and pass to client.listOgcItems().",
      );
    }
    return this.boundClient.listOgcItems(this.build());
  }
}
