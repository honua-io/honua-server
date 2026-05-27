// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Canonical resource categories in the Metadata v2 graph.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MetadataV2ResourceType>))]
public enum MetadataV2ResourceType
{
    /// <summary>
    /// A vector feature dataset whose physical storage is described by one or more storage bindings.
    /// </summary>
    [JsonStringEnumMemberName("feature-dataset")]
    FeatureDataset,

    /// <summary>
    /// A tabular resource without required geometry.
    /// </summary>
    [JsonStringEnumMemberName("table")]
    Table,

    /// <summary>
    /// A raster or gridded dataset.
    /// </summary>
    [JsonStringEnumMemberName("raster-dataset")]
    RasterDataset,

    /// <summary>
    /// A tile dataset.
    /// </summary>
    [JsonStringEnumMemberName("tile-dataset")]
    TileDataset,

    /// <summary>
    /// A process resource.
    /// </summary>
    [JsonStringEnumMemberName("process")]
    Process,

    /// <summary>
    /// A reusable style resource.
    /// </summary>
    [JsonStringEnumMemberName("style")]
    Style,

    /// <summary>
    /// A document resource.
    /// </summary>
    [JsonStringEnumMemberName("document")]
    Document,

    /// <summary>
    /// An external resource represented in the metadata graph.
    /// </summary>
    [JsonStringEnumMemberName("external-resource")]
    ExternalResource,

    /// <summary>
    /// A saved map composition or map document.
    /// </summary>
    [JsonStringEnumMemberName("map")]
    Map,

    /// <summary>
    /// A dashboard composed from one or more data sources.
    /// </summary>
    [JsonStringEnumMemberName("dashboard")]
    Dashboard,

    /// <summary>
    /// A field-collection or data-entry form.
    /// </summary>
    [JsonStringEnumMemberName("form")]
    Form,

    /// <summary>
    /// A generated or configured application surface.
    /// </summary>
    [JsonStringEnumMemberName("app")]
    App,

    /// <summary>
    /// A workflow definition.
    /// </summary>
    [JsonStringEnumMemberName("workflow")]
    Workflow,

    /// <summary>
    /// A geoprocessing service definition.
    /// </summary>
    [JsonStringEnumMemberName("geoprocessing-service")]
    GeoprocessingService,

    /// <summary>
    /// An ETL pipeline definition.
    /// </summary>
    [JsonStringEnumMemberName("etl-pipeline")]
    EtlPipeline
}

/// <summary>
/// External or managed connection categories used by storage bindings.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MetadataV2ConnectionType>))]
public enum MetadataV2ConnectionType
{
    /// <summary>
    /// A database connection.
    /// </summary>
    [JsonStringEnumMemberName("database")]
    Database,

    /// <summary>
    /// Object storage such as S3, Azure Blob Storage, or Google Cloud Storage.
    /// </summary>
    [JsonStringEnumMemberName("object-storage")]
    ObjectStorage,

    /// <summary>
    /// Local or network file storage.
    /// </summary>
    [JsonStringEnumMemberName("file-system")]
    FileSystem,

    /// <summary>
    /// An external HTTP API.
    /// </summary>
    [JsonStringEnumMemberName("http-api")]
    HttpApi,

    /// <summary>
    /// A STAC catalog or API.
    /// </summary>
    [JsonStringEnumMemberName("stac")]
    Stac,

    /// <summary>
    /// A managed Honua platform connection.
    /// </summary>
    [JsonStringEnumMemberName("managed")]
    Managed
}

/// <summary>
/// Physical storage formats and access patterns for Metadata v2 storage bindings.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MetadataV2StorageType>))]
public enum MetadataV2StorageType
{
    /// <summary>
    /// A relational database table.
    /// </summary>
    [JsonStringEnumMemberName("relational-table")]
    RelationalTable,

    /// <summary>
    /// A SQL view.
    /// </summary>
    [JsonStringEnumMemberName("sql-view")]
    SqlView,

    /// <summary>
    /// A SQL query.
    /// </summary>
    [JsonStringEnumMemberName("sql-query")]
    SqlQuery,

    /// <summary>
    /// A GeoPackage table.
    /// </summary>
    [JsonStringEnumMemberName("geopackage-table")]
    GeoPackageTable,

    /// <summary>
    /// A GeoJSON document or sequence.
    /// </summary>
    [JsonStringEnumMemberName("geojson")]
    GeoJson,

    /// <summary>
    /// A GeoParquet dataset.
    /// </summary>
    [JsonStringEnumMemberName("geoparquet")]
    GeoParquet,

    /// <summary>
    /// An Apache Arrow dataset.
    /// </summary>
    [JsonStringEnumMemberName("arrow")]
    Arrow,

    /// <summary>
    /// A cloud optimized GeoTIFF.
    /// </summary>
    [JsonStringEnumMemberName("cloud-optimized-geotiff")]
    CloudOptimizedGeoTiff,

    /// <summary>
    /// A Zarr dataset.
    /// </summary>
    [JsonStringEnumMemberName("zarr")]
    Zarr,

    /// <summary>
    /// A NetCDF dataset.
    /// </summary>
    [JsonStringEnumMemberName("netcdf")]
    NetCdf,

    /// <summary>
    /// An MBTiles archive.
    /// </summary>
    [JsonStringEnumMemberName("mbtiles")]
    MbTiles,

    /// <summary>
    /// A PMTiles archive.
    /// </summary>
    [JsonStringEnumMemberName("pmtiles")]
    PmTiles,

    /// <summary>
    /// A tile cache.
    /// </summary>
    [JsonStringEnumMemberName("tile-cache")]
    TileCache,

    /// <summary>
    /// An object storage prefix.
    /// </summary>
    [JsonStringEnumMemberName("object-prefix")]
    ObjectPrefix,

    /// <summary>
    /// An external API-backed storage source.
    /// </summary>
    [JsonStringEnumMemberName("external-api")]
    ExternalApi,

    /// <summary>
    /// A STAC asset.
    /// </summary>
    [JsonStringEnumMemberName("stac-asset")]
    StacAsset
}

/// <summary>
/// Public service categories. These are intentionally separate from storage types.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MetadataV2ServiceType>))]
public enum MetadataV2ServiceType
{
    /// <summary>
    /// An OGC API Features service.
    /// </summary>
    [JsonStringEnumMemberName("ogc-api-features")]
    OgcApiFeatures,

    /// <summary>
    /// A WFS service.
    /// </summary>
    [JsonStringEnumMemberName("wfs")]
    Wfs,

    /// <summary>
    /// A WMS service.
    /// </summary>
    [JsonStringEnumMemberName("wms")]
    Wms,

    /// <summary>
    /// A WMTS service.
    /// </summary>
    [JsonStringEnumMemberName("wmts")]
    Wmts,

    /// <summary>
    /// An Esri feature service.
    /// </summary>
    [JsonStringEnumMemberName("esri-feature-service")]
    EsriFeatureService,

    /// <summary>
    /// An Esri map service.
    /// </summary>
    [JsonStringEnumMemberName("esri-map-service")]
    EsriMapService,

    /// <summary>
    /// An Esri image service.
    /// </summary>
    [JsonStringEnumMemberName("esri-image-service")]
    EsriImageService,

    /// <summary>
    /// A STAC API.
    /// </summary>
    [JsonStringEnumMemberName("stac-api")]
    StacApi,

    /// <summary>
    /// A DCAT catalog.
    /// </summary>
    [JsonStringEnumMemberName("dcat-catalog")]
    DcatCatalog,

    /// <summary>
    /// An OGC Records catalog service.
    /// </summary>
    [JsonStringEnumMemberName("ogc-records")]
    OgcRecords,

    /// <summary>
    /// An OData service.
    /// </summary>
    [JsonStringEnumMemberName("odata")]
    OData,

    /// <summary>
    /// A custom or extension-defined service.
    /// </summary>
    [JsonStringEnumMemberName("custom")]
    Custom
}

/// <summary>
/// Publication categories exposed by services. These are intentionally separate from storage types.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MetadataV2PublicationType>))]
public enum MetadataV2PublicationType
{
    /// <summary>
    /// An OGC collection publication.
    /// </summary>
    [JsonStringEnumMemberName("ogc-collection")]
    OgcCollection,

    /// <summary>
    /// A WFS feature type publication.
    /// </summary>
    [JsonStringEnumMemberName("wfs-feature-type")]
    WfsFeatureType,

    /// <summary>
    /// A WMS layer publication.
    /// </summary>
    [JsonStringEnumMemberName("wms-layer")]
    WmsLayer,

    /// <summary>
    /// A WMTS layer publication.
    /// </summary>
    [JsonStringEnumMemberName("wmts-layer")]
    WmtsLayer,

    /// <summary>
    /// An Esri feature layer publication.
    /// </summary>
    [JsonStringEnumMemberName("esri-feature-layer")]
    EsriFeatureLayer,

    /// <summary>
    /// An Esri map layer publication.
    /// </summary>
    [JsonStringEnumMemberName("esri-map-layer")]
    EsriMapLayer,

    /// <summary>
    /// An Esri image layer publication.
    /// </summary>
    [JsonStringEnumMemberName("esri-image-layer")]
    EsriImageLayer,

    /// <summary>
    /// A STAC collection publication.
    /// </summary>
    [JsonStringEnumMemberName("stac-collection")]
    StacCollection,

    /// <summary>
    /// A DCAT distribution publication.
    /// </summary>
    [JsonStringEnumMemberName("dcat-distribution")]
    DcatDistribution,

    /// <summary>
    /// An OGC record publication.
    /// </summary>
    [JsonStringEnumMemberName("ogc-record")]
    OgcRecord,

    /// <summary>
    /// An OData entity set publication.
    /// </summary>
    [JsonStringEnumMemberName("odata-entity-set")]
    ODataEntitySet,

    /// <summary>
    /// A custom or extension-defined publication.
    /// </summary>
    [JsonStringEnumMemberName("custom")]
    Custom
}

/// <summary>
/// Capabilities supported by a Metadata v2 storage binding.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MetadataV2StorageBindingCapability>))]
public enum MetadataV2StorageBindingCapability
{
    /// <summary>
    /// The binding can execute read queries.
    /// </summary>
    [JsonStringEnumMemberName("query")]
    Query,

    /// <summary>
    /// The binding can filter records or pixels server-side.
    /// </summary>
    [JsonStringEnumMemberName("filter")]
    Filter,

    /// <summary>
    /// The binding can sort results server-side.
    /// </summary>
    [JsonStringEnumMemberName("sort")]
    Sort,

    /// <summary>
    /// The binding can aggregate results server-side.
    /// </summary>
    [JsonStringEnumMemberName("aggregate")]
    Aggregate,

    /// <summary>
    /// The binding supports edits.
    /// </summary>
    [JsonStringEnumMemberName("edit")]
    Edit,

    /// <summary>
    /// The binding supports transactional edits.
    /// </summary>
    [JsonStringEnumMemberName("transactions")]
    Transactions,

    /// <summary>
    /// The binding can render map or coverage outputs.
    /// </summary>
    [JsonStringEnumMemberName("render")]
    Render,

    /// <summary>
    /// The binding can serve tiles.
    /// </summary>
    [JsonStringEnumMemberName("tile")]
    Tile,

    /// <summary>
    /// The binding can provide downloadable artifacts.
    /// </summary>
    [JsonStringEnumMemberName("download")]
    Download,

    /// <summary>
    /// The binding supports search.
    /// </summary>
    [JsonStringEnumMemberName("search")]
    Search
}

/// <summary>
/// Lifecycle values shared by Metadata v2 graph entities.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MetadataV2LifecycleStatus>))]
public enum MetadataV2LifecycleStatus
{
    /// <summary>
    /// The entity is being drafted and should not be treated as active.
    /// </summary>
    [JsonStringEnumMemberName("draft")]
    Draft,

    /// <summary>
    /// The entity is active.
    /// </summary>
    [JsonStringEnumMemberName("active")]
    Active,

    /// <summary>
    /// The entity is deprecated but still available.
    /// </summary>
    [JsonStringEnumMemberName("deprecated")]
    Deprecated,

    /// <summary>
    /// The entity is retired.
    /// </summary>
    [JsonStringEnumMemberName("retired")]
    Retired,

    /// <summary>
    /// The entity is archived.
    /// </summary>
    [JsonStringEnumMemberName("archived")]
    Archived
}

/// <summary>
/// Observed operational state for Metadata v2 graph entities.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MetadataV2OperationalState>))]
public enum MetadataV2OperationalState
{
    /// <summary>
    /// State has not been observed yet.
    /// </summary>
    [JsonStringEnumMemberName("unknown")]
    Unknown,

    /// <summary>
    /// The entity is ready.
    /// </summary>
    [JsonStringEnumMemberName("ready")]
    Ready,

    /// <summary>
    /// The entity is pending reconciliation.
    /// </summary>
    [JsonStringEnumMemberName("pending")]
    Pending,

    /// <summary>
    /// The entity is degraded.
    /// </summary>
    [JsonStringEnumMemberName("degraded")]
    Degraded,

    /// <summary>
    /// The entity failed reconciliation or health checks.
    /// </summary>
    [JsonStringEnumMemberName("failed")]
    Failed
}

/// <summary>
/// Canonical Metadata v2 geometry type. Mirrors the SFA/OGC simple-feature geometry
/// taxonomy plus a couple of catch-all values for heterogeneous and unspecified data.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MetadataV2GeometryType>))]
public enum MetadataV2GeometryType
{
    /// <summary>The resource is not geometric or geometry type is unspecified.</summary>
    [JsonStringEnumMemberName("none")]
    None,

    /// <summary>Single-point geometry.</summary>
    [JsonStringEnumMemberName("point")]
    Point,

    /// <summary>Multi-point geometry.</summary>
    [JsonStringEnumMemberName("multipoint")]
    MultiPoint,

    /// <summary>Single-line-string geometry.</summary>
    [JsonStringEnumMemberName("linestring")]
    LineString,

    /// <summary>Multi-line-string geometry.</summary>
    [JsonStringEnumMemberName("multilinestring")]
    MultiLineString,

    /// <summary>Single-polygon geometry.</summary>
    [JsonStringEnumMemberName("polygon")]
    Polygon,

    /// <summary>Multi-polygon geometry.</summary>
    [JsonStringEnumMemberName("multipolygon")]
    MultiPolygon,

    /// <summary>Heterogeneous geometry collection.</summary>
    [JsonStringEnumMemberName("geometrycollection")]
    GeometryCollection,

    /// <summary>The dataset mixes multiple geometry types.</summary>
    [JsonStringEnumMemberName("mixed")]
    Mixed
}

/// <summary>
/// Canonical Metadata v2 field type. Mirrors the runtime <c>FieldType</c> enum used by the
/// query pipeline but lives in the metadata domain so the graph is self-contained.
/// String-encoded for JSON to keep older snapshots readable; the enum is the source of
/// truth in code.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MetadataV2FieldType>))]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Mirrors the v1 FieldType enum which uses canonical GIS field-type names.")]
public enum MetadataV2FieldType
{
    /// <summary>Unknown or unspecified field type.</summary>
    [JsonStringEnumMemberName("unknown")]
    Unknown,

    /// <summary>Text/character field.</summary>
    [JsonStringEnumMemberName("string")]
    String,

    /// <summary>32-bit integer.</summary>
    [JsonStringEnumMemberName("integer")]
    Integer,

    /// <summary>64-bit integer.</summary>
    [JsonStringEnumMemberName("biginteger")]
    BigInteger,

    /// <summary>64-bit floating-point.</summary>
    [JsonStringEnumMemberName("double")]
    Double,

    /// <summary>32-bit floating-point.</summary>
    [JsonStringEnumMemberName("float")]
    Float,

    /// <summary>Boolean.</summary>
    [JsonStringEnumMemberName("boolean")]
    Boolean,

    /// <summary>Timestamp with date and time.</summary>
    [JsonStringEnumMemberName("datetime")]
    DateTime,

    /// <summary>Calendar date without time.</summary>
    [JsonStringEnumMemberName("date")]
    Date,

    /// <summary>Time of day without date.</summary>
    [JsonStringEnumMemberName("time")]
    Time,

    /// <summary>JSON document.</summary>
    [JsonStringEnumMemberName("json")]
    Json,

    /// <summary>Binary blob.</summary>
    [JsonStringEnumMemberName("binary")]
    Binary,

    /// <summary>UUID/GUID.</summary>
    [JsonStringEnumMemberName("uuid")]
    Uuid,

    /// <summary>Geometry value (WKB/WKT/EWKB/etc).</summary>
    [JsonStringEnumMemberName("geometry")]
    Geometry,

    /// <summary>Geography value (spherical-coordinate geometry).</summary>
    [JsonStringEnumMemberName("geography")]
    Geography
}
