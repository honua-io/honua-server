// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

[JsonConverter(typeof(JsonStringEnumConverter<MetadataV2ResourceType>))]
public enum MetadataV2ResourceType
{
    [JsonStringEnumMemberName("feature-dataset")]
    FeatureDataset,

    [JsonStringEnumMemberName("table")]
    Table,

    [JsonStringEnumMemberName("raster-dataset")]
    RasterDataset,

    [JsonStringEnumMemberName("tile-dataset")]
    TileDataset,

    [JsonStringEnumMemberName("process")]
    Process,

    [JsonStringEnumMemberName("style")]
    Style,

    [JsonStringEnumMemberName("document")]
    Document,

    [JsonStringEnumMemberName("external-resource")]
    ExternalResource,

    [JsonStringEnumMemberName("map")]
    Map,

    [JsonStringEnumMemberName("dashboard")]
    Dashboard,

    [JsonStringEnumMemberName("form")]
    Form,

    [JsonStringEnumMemberName("app")]
    App,

    [JsonStringEnumMemberName("workflow")]
    Workflow,

    [JsonStringEnumMemberName("geoprocessing-service")]
    GeoprocessingService,

    [JsonStringEnumMemberName("etl-pipeline")]
    EtlPipeline,

    [JsonStringEnumMemberName("unknown")]
    Unknown = -1
}

[JsonConverter(typeof(JsonStringEnumConverter<MetadataV2ConnectionType>))]
public enum MetadataV2ConnectionType
{
    [JsonStringEnumMemberName("database")]
    Database,

    [JsonStringEnumMemberName("object-storage")]
    ObjectStorage,

    [JsonStringEnumMemberName("file-system")]
    FileSystem,

    [JsonStringEnumMemberName("http-api")]
    HttpApi,

    [JsonStringEnumMemberName("stac")]
    Stac,

    [JsonStringEnumMemberName("managed")]
    Managed,

    [JsonStringEnumMemberName("unknown")]
    Unknown = -1
}

[JsonConverter(typeof(JsonStringEnumConverter<MetadataV2StorageType>))]
public enum MetadataV2StorageType
{
    [JsonStringEnumMemberName("relational-table")]
    RelationalTable,

    [JsonStringEnumMemberName("sql-view")]
    SqlView,

    [JsonStringEnumMemberName("sql-query")]
    SqlQuery,

    [JsonStringEnumMemberName("geopackage-table")]
    GeoPackageTable,

    [JsonStringEnumMemberName("geojson")]
    GeoJson,

    [JsonStringEnumMemberName("geoparquet")]
    GeoParquet,

    [JsonStringEnumMemberName("arrow")]
    Arrow,

    [JsonStringEnumMemberName("cloud-optimized-geotiff")]
    CloudOptimizedGeoTiff,

    [JsonStringEnumMemberName("zarr")]
    Zarr,

    [JsonStringEnumMemberName("netcdf")]
    NetCdf,

    [JsonStringEnumMemberName("mbtiles")]
    MbTiles,

    [JsonStringEnumMemberName("pmtiles")]
    PmTiles,

    [JsonStringEnumMemberName("tile-cache")]
    TileCache,

    [JsonStringEnumMemberName("object-prefix")]
    ObjectPrefix,

    [JsonStringEnumMemberName("external-api")]
    ExternalApi,

    [JsonStringEnumMemberName("stac-asset")]
    StacAsset,

    [JsonStringEnumMemberName("unknown")]
    Unknown = -1
}

[JsonConverter(typeof(JsonStringEnumConverter<MetadataV2ServiceType>))]
public enum MetadataV2ServiceType
{
    [JsonStringEnumMemberName("ogc-api-features")]
    OgcApiFeatures,

    [JsonStringEnumMemberName("wfs")]
    Wfs,

    [JsonStringEnumMemberName("wms")]
    Wms,

    [JsonStringEnumMemberName("wmts")]
    Wmts,

    [JsonStringEnumMemberName("esri-feature-service")]
    EsriFeatureService,

    [JsonStringEnumMemberName("esri-map-service")]
    EsriMapService,

    [JsonStringEnumMemberName("esri-image-service")]
    EsriImageService,

    [JsonStringEnumMemberName("stac-api")]
    StacApi,

    [JsonStringEnumMemberName("dcat-catalog")]
    DcatCatalog,

    [JsonStringEnumMemberName("ogc-records")]
    OgcRecords,

    [JsonStringEnumMemberName("odata")]
    OData,

    [JsonStringEnumMemberName("sensorthings")]
    SensorThings,

    [JsonStringEnumMemberName("custom")]
    Custom,

    [JsonStringEnumMemberName("unknown")]
    Unknown = -1
}

[JsonConverter(typeof(JsonStringEnumConverter<MetadataV2LayerType>))]
public enum MetadataV2LayerType
{
    [JsonStringEnumMemberName("ogc-collection")]
    OgcCollection,

    [JsonStringEnumMemberName("wfs-feature-type")]
    WfsFeatureType,

    [JsonStringEnumMemberName("wms-layer")]
    WmsLayer,

    [JsonStringEnumMemberName("wmts-layer")]
    WmtsLayer,

    [JsonStringEnumMemberName("esri-feature-layer")]
    EsriFeatureLayer,

    [JsonStringEnumMemberName("esri-map-layer")]
    EsriMapLayer,

    [JsonStringEnumMemberName("esri-image-layer")]
    EsriImageLayer,

    [JsonStringEnumMemberName("esri-vector-tile-layer")]
    EsriVectorTileLayer,

    [JsonStringEnumMemberName("stac-collection")]
    StacCollection,

    [JsonStringEnumMemberName("dcat-distribution")]
    DcatDistribution,

    [JsonStringEnumMemberName("ogc-record")]
    OgcRecord,

    [JsonStringEnumMemberName("odata-entity-set")]
    ODataEntitySet,

    [JsonStringEnumMemberName("sensorthings-datastream")]
    SensorThingsDatastream,

    [JsonStringEnumMemberName("custom")]
    Custom,

    [JsonStringEnumMemberName("unknown")]
    Unknown = -1
}

[JsonConverter(typeof(JsonStringEnumConverter<MetadataV2StorageBindingCapability>))]
public enum MetadataV2StorageBindingCapability
{
    [JsonStringEnumMemberName("query")]
    Query,

    [JsonStringEnumMemberName("filter")]
    Filter,

    [JsonStringEnumMemberName("sort")]
    Sort,

    [JsonStringEnumMemberName("aggregate")]
    Aggregate,

    [JsonStringEnumMemberName("edit")]
    Edit,

    [JsonStringEnumMemberName("transactions")]
    Transactions,

    [JsonStringEnumMemberName("render")]
    Render,

    [JsonStringEnumMemberName("tile")]
    Tile,

    [JsonStringEnumMemberName("download")]
    Download,

    [JsonStringEnumMemberName("search")]
    Search,

    [JsonStringEnumMemberName("unknown")]
    Unknown = -1
}

[JsonConverter(typeof(JsonStringEnumConverter<MetadataV2LifecycleStatus>))]
public enum MetadataV2LifecycleStatus
{
    [JsonStringEnumMemberName("draft")]
    Draft,

    [JsonStringEnumMemberName("active")]
    Active,

    [JsonStringEnumMemberName("deprecated")]
    Deprecated,

    [JsonStringEnumMemberName("retired")]
    Retired,

    [JsonStringEnumMemberName("archived")]
    Archived,

    [JsonStringEnumMemberName("unknown")]
    Unknown = -1
}

[JsonConverter(typeof(JsonStringEnumConverter<MetadataV2OperationalState>))]
public enum MetadataV2OperationalState
{
    [JsonStringEnumMemberName("unknown")]
    Unknown,

    [JsonStringEnumMemberName("ready")]
    Ready,

    [JsonStringEnumMemberName("pending")]
    Pending,

    [JsonStringEnumMemberName("degraded")]
    Degraded,

    [JsonStringEnumMemberName("failed")]
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter<MetadataV2GeometryType>))]
public enum MetadataV2GeometryType
{
    [JsonStringEnumMemberName("none")]
    None,

    [JsonStringEnumMemberName("point")]
    Point,

    [JsonStringEnumMemberName("multipoint")]
    MultiPoint,

    [JsonStringEnumMemberName("linestring")]
    LineString,

    [JsonStringEnumMemberName("multilinestring")]
    MultiLineString,

    [JsonStringEnumMemberName("polygon")]
    Polygon,

    [JsonStringEnumMemberName("multipolygon")]
    MultiPolygon,

    [JsonStringEnumMemberName("geometrycollection")]
    GeometryCollection,

    [JsonStringEnumMemberName("mixed")]
    Mixed,

    [JsonStringEnumMemberName("unknown")]
    Unknown = -1
}

[JsonConverter(typeof(JsonStringEnumConverter<MetadataV2FieldType>))]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Mirrors the v1 FieldType enum which uses canonical GIS field-type names.")]
public enum MetadataV2FieldType
{
    [JsonStringEnumMemberName("unknown")]
    Unknown,

    [JsonStringEnumMemberName("string")]
    String,

    [JsonStringEnumMemberName("integer")]
    Integer,

    [JsonStringEnumMemberName("biginteger")]
    BigInteger,

    [JsonStringEnumMemberName("double")]
    Double,

    [JsonStringEnumMemberName("float")]
    Float,

    [JsonStringEnumMemberName("boolean")]
    Boolean,

    [JsonStringEnumMemberName("datetime")]
    DateTime,

    [JsonStringEnumMemberName("date")]
    Date,

    [JsonStringEnumMemberName("time")]
    Time,

    [JsonStringEnumMemberName("json")]
    Json,

    [JsonStringEnumMemberName("binary")]
    Binary,

    [JsonStringEnumMemberName("uuid")]
    Uuid,

    [JsonStringEnumMemberName("geometry")]
    Geometry,

    [JsonStringEnumMemberName("geography")]
    Geography
}
