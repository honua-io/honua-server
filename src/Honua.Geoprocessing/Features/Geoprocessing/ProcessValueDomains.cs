// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Geoprocessing;

// The published token list is the complete accepted set, aliases included, because a narrower list would make a choice check reject values the validator accepts.
internal static class ProcessValueDomains
{
    internal static readonly IReadOnlyList<string> ClusterAlgorithm = ["dbscan", "kmeans", "k-means"];

    internal static readonly IReadOnlyList<string> SpatialJoinPredicate =
        ["intersects", "contains", "within", "dwithin"];

    internal static readonly IReadOnlyList<string> ManagedSpatialJoinPredicate =
        ["intersects", "contains", "within"];

    internal static readonly IReadOnlyList<string> EnrichmentMethod =
    [
        "intersects", "point-in-polygon", "point_in_polygon", "pip", "contains", "within",
        "within-distance", "within_distance", "dwithin",
        "nearest-neighbor", "nearest_neighbor", "nearest"
    ];

    internal static readonly IReadOnlyList<string> EnrichmentPredicate =
        ["intersects", "contains", "within", "dwithin"];

    // These names are not published as an allowed-value list because the parameter is a semicolon-separated field:stat list and a choice list would reject every legal value.
    internal static readonly IReadOnlyList<string> ManagedSpatialJoinStat =
        ["count", "sum", "mean", "avg", "average", "min", "max"];

    internal static readonly IReadOnlyList<string> DensityMode =
        ["hex", "hexgrid", "hex-grid", "square", "squaregrid", "square-grid"];

    internal static readonly IReadOnlyList<string> BufferAggregateUnit =
    [
        "meters", "meter", "m",
        "kilometers", "kilometer", "km",
        "feet", "foot", "ft",
        "miles", "mile", "mi"
    ];

    internal static readonly IReadOnlyList<string> SurfaceSlopeUnit = ["degrees", "degree", "percent"];

    internal static readonly IReadOnlyList<string> RasterResampling =
    [
        "nearestneighbor", "nearest-neighbor", "nearest",
        "bilinear",
        "cubic", "bicubic",
        "lanczos"
    ];

    internal static readonly IReadOnlyList<string> RasterMosaicOperator = ["first", "last"];

    internal static readonly IReadOnlyList<string> KrigingVariogramModel =
        ["spherical", "exponential", "gaussian"];

    internal static readonly IReadOnlyList<string> RasterFormat =
    [
        "GTiff", "GeoTIFF", "TIFF", "TIF",
        "PNG",
        "JPEG", "JPG",
        "COG"
    ];

    internal static readonly IReadOnlyList<string> RasterCalcDataType =
        ["Byte", "Int16", "UInt16", "Int32", "UInt32", "Float32", "Float64"];

    internal static readonly IReadOnlyList<string> SpectralIndex = ["NDVI", "NDWI", "NDBI", "SAVI", "EVI"];

    internal static readonly IReadOnlyList<string> ProximityDistanceUnit = ["GEO", "PIXEL"];

    internal static readonly IReadOnlyList<string> GeometryFormat = ["wkt", "geojson", "wkb", "ewkt"];

    // Not published as single-token AllowedValues, because the parameter is a comma-separated list and a choice list would reject every legal value, including the default.
    internal static readonly IReadOnlyList<string> RasterZonalStatistic =
        ["count", "sum", "mean", "min", "max", "stddev", "variance"];

    internal static readonly IReadOnlyList<string> ImageryClassifyTask =
        ["classification", "segmentation", "detection"];

    internal static readonly IReadOnlyList<string> PolygonizeConnectedness = ["4", "8"];

    internal static readonly IReadOnlyList<string> AttributeCastTargetType =
        ["int", "long", "double", "bool", "string"];

    internal static readonly IReadOnlyList<string> AttributeCastOnError = ["drop", "null", "keep"];

    internal static readonly IReadOnlyList<string> AttributeFilterOp =
        ["eq", "neq", "gt", "gte", "lt", "lte", "contains", "exists"];

    internal static readonly IReadOnlyList<string> SpatialFilterPredicate = ["intersects", "within"];

    // Not published as AllowedValues, because the executor also accepts op=expression, which this set omits.
    internal static readonly IReadOnlyList<string> ComputedFieldOp =
        ["concat", "add", "subtract", "multiply", "divide", "const"];
}
