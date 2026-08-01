// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geoprocessing;

/// <summary>
/// Single source of truth for the finite value domains the geoprocessing surface enforces.
/// <see cref="ProcessPlanValidator"/> builds its enum allow-lists from these arrays and
/// <see cref="BuiltInProcessCatalog"/> publishes the same arrays as
/// <c>ProcessParameterSpec.AllowedValues</c>, so the enforced domain and the advertised
/// domain cannot drift apart (#3048).
/// </summary>
/// <remarks>
/// <para>
/// Each array is the COMPLETE accepted token set, aliases included, not a curated display
/// list. Publishing a narrower set would make protocol adapters that enforce
/// <c>AllowedValues</c> — GPServer's GPChoice check — reject values the canonical validator
/// accepts, which would change runtime behavior rather than merely describing it. Comparison
/// stays case-insensitive at every enforcement point, so the casing here is purely the
/// canonical spelling used for display.
/// </para>
/// <para>
/// A domain declared here becomes enumerable for
/// <see cref="Honua.Core.Features.Geoprocessing.Abstractions.IProcessConditionalInputProbe"/>,
/// which lets the toolbox translation lane prove a mapping branch-by-branch instead of
/// answering pessimistically. Three enforced token sets are deliberately NOT published; see
/// <see cref="ManagedSpatialJoinStat"/>, <see cref="RasterZonalStatistic"/> and
/// <see cref="ComputedFieldOp"/> for the reasons.
/// </para>
/// </remarks>
internal static class ProcessValueDomains
{
    /// <summary>
    /// Clustering algorithms accepted by <c>analytics.cluster</c> and
    /// <c>analytics.cluster-managed</c>. The discriminator the conditional
    /// <c>eps</c>/<c>minPoints</c> (dbscan) and <c>k</c> (kmeans) requirements branch on.
    /// </summary>
    internal static readonly IReadOnlyList<string> ClusterAlgorithm = ["dbscan", "kmeans", "k-means"];

    /// <summary>
    /// Spatial predicates accepted by the PostGIS-backed <c>analytics.spatial-join</c>.
    /// <c>dwithin</c> is the discriminator that makes <c>distance</c> required.
    /// </summary>
    internal static readonly IReadOnlyList<string> SpatialJoinPredicate =
        ["intersects", "contains", "within", "dwithin"];

    /// <summary>
    /// Spatial predicates accepted by <c>analytics.spatial-join-managed</c>. Narrower than
    /// <see cref="SpatialJoinPredicate"/> because the managed join has no distance support.
    /// </summary>
    internal static readonly IReadOnlyList<string> ManagedSpatialJoinPredicate =
        ["intersects", "contains", "within"];

    /// <summary>
    /// Aggregate statistics accepted inside <c>analytics.spatial-join-managed</c>'s
    /// <c>statistics</c> spec. NOT published as <c>AllowedValues</c>: the parameter carries a
    /// semicolon-separated list of <c>field:stat</c> pairs rather than a single token, so a
    /// choice list over these names would reject every legal value.
    /// </summary>
    internal static readonly IReadOnlyList<string> ManagedSpatialJoinStat =
        ["count", "sum", "mean", "avg", "average", "min", "max"];

    /// <summary>
    /// Binning modes accepted by <c>analytics.density</c> and <c>analytics.density-managed</c>.
    /// </summary>
    internal static readonly IReadOnlyList<string> DensityMode =
        ["hex", "hexgrid", "hex-grid", "square", "squaregrid", "square-grid"];

    /// <summary>
    /// Linear units accepted by <c>analytics.buffer-aggregate</c> and
    /// <c>analytics.buffer-aggregate-managed</c>.
    /// </summary>
    internal static readonly IReadOnlyList<string> BufferAggregateUnit =
    [
        "meters", "meter", "m",
        "kilometers", "kilometer", "km",
        "feet", "foot", "ft",
        "miles", "mile", "mi"
    ];

    /// <summary>
    /// Slope units accepted by <c>surface.slope</c>. gdaldem emits degrees by default and
    /// percent under <c>-p</c>; radians are not a first-class gdaldem output.
    /// </summary>
    internal static readonly IReadOnlyList<string> SurfaceSlopeUnit = ["degrees", "degree", "percent"];

    /// <summary>
    /// Resampling algorithms accepted by the gdalwarp-backed raster processes
    /// (<c>raster.reproject</c>, <c>raster.resample</c>, <c>raster.mosaic</c>,
    /// <c>conversion.raster-reproject</c>).
    /// </summary>
    internal static readonly IReadOnlyList<string> RasterResampling =
    [
        "nearestneighbor", "nearest-neighbor", "nearest",
        "bilinear",
        "cubic", "bicubic",
        "lanczos"
    ];

    /// <summary>
    /// Overlap operators <c>raster.mosaic</c> can express through gdalwarp source ordering.
    /// Statistical operators are not available on the native worker and stay rejected.
    /// </summary>
    internal static readonly IReadOnlyList<string> RasterMosaicOperator = ["first", "last"];

    /// <summary>
    /// Output raster formats accepted by <c>conversion.raster-format</c>.
    /// </summary>
    internal static readonly IReadOnlyList<string> RasterFormat =
    [
        "GTiff", "GeoTIFF", "TIFF", "TIF",
        "PNG",
        "JPEG", "JPG",
        "COG"
    ];

    /// <summary>
    /// <c>gdal_calc.py --type</c> values accepted by the calc-family processes
    /// (<c>raster.map-algebra</c>, <c>raster.reclassify</c>).
    /// </summary>
    internal static readonly IReadOnlyList<string> RasterCalcDataType =
        ["Byte", "Int16", "UInt16", "Int32", "UInt32", "Float32", "Float64"];

    /// <summary>
    /// Spectral-index presets <c>raster.spectral-index</c> recognizes. The discriminator the
    /// per-preset band-role requirements (<c>nir</c>/<c>red</c>/<c>green</c>/<c>swir</c>/<c>blue</c>)
    /// branch on.
    /// </summary>
    internal static readonly IReadOnlyList<string> SpectralIndex = ["NDVI", "NDWI", "NDBI", "SAVI", "EVI"];

    /// <summary>
    /// Euclidean-distance units passed to <c>gdal_proximity -distunits</c> by
    /// <c>proximity.euclidean-distance</c> and <c>proximity.euclidean-allocation</c>.
    /// </summary>
    internal static readonly IReadOnlyList<string> ProximityDistanceUnit = ["GEO", "PIXEL"];

    /// <summary>
    /// Target encodings accepted by <c>conversion.geometry-format</c>.
    /// </summary>
    internal static readonly IReadOnlyList<string> GeometryFormat = ["wkt", "geojson", "wkb", "ewkt"];

    /// <summary>
    /// Zonal aggregates accepted by <c>raster.zonal-statistics</c>. NOT published as
    /// <c>AllowedValues</c>: the parameter takes a COMMA-SEPARATED list (its own default is
    /// <c>count,mean,stddev,min,max,sum</c>), so a single-token choice list would reject every
    /// legal value including the default.
    /// </summary>
    internal static readonly IReadOnlyList<string> RasterZonalStatistic =
        ["count", "sum", "mean", "min", "max", "stddev", "variance"];

    /// <summary>
    /// Inference tasks accepted by <c>imagery.classify</c>. Matched case-SENSITIVELY, matching
    /// the cloud backends' task tokens.
    /// </summary>
    internal static readonly IReadOnlyList<string> ImageryClassifyTask =
        ["classification", "segmentation", "detection"];

    /// <summary>
    /// Pixel connectedness accepted by <c>conversion.polygonize</c>.
    /// </summary>
    internal static readonly IReadOnlyList<string> PolygonizeConnectedness = ["4", "8"];

    /// <summary>
    /// Target CLR types accepted by <c>transform.attribute-cast</c>.
    /// </summary>
    internal static readonly IReadOnlyList<string> AttributeCastTargetType =
        ["int", "long", "double", "bool", "string"];

    /// <summary>
    /// Uncoercible-row policies accepted by <c>transform.attribute-cast</c>.
    /// </summary>
    internal static readonly IReadOnlyList<string> AttributeCastOnError = ["drop", "null", "keep"];

    /// <summary>
    /// Comparison operators accepted by <c>transform.attribute-filter</c>.
    /// </summary>
    internal static readonly IReadOnlyList<string> AttributeFilterOp =
        ["eq", "neq", "gt", "gte", "lt", "lte", "contains", "exists"];

    /// <summary>
    /// Spatial predicates accepted by <c>transform.spatial-filter</c>.
    /// </summary>
    internal static readonly IReadOnlyList<string> SpatialFilterPredicate = ["intersects", "within"];

    /// <summary>
    /// Fixed computation ops <see cref="ProcessPlanValidator"/> accepts for
    /// <c>transform.computed-field</c>. NOT published as <c>AllowedValues</c>: the executor
    /// also honors <c>op=expression</c> (and infers it when <c>expression</c> is supplied),
    /// which this set omits, so publishing it would advertise a domain narrower than the
    /// process actually supports. Widening the validator is a behavior change and stays out of
    /// the catalog-metadata scope; until then <c>op</c> remains an unenumerable discriminator
    /// and the translation lane answers conservatively for it.
    /// </summary>
    internal static readonly IReadOnlyList<string> ComputedFieldOp =
        ["concat", "add", "subtract", "multiply", "divide", "const"];
}
