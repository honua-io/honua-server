// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.ObjectModel;
using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Postgres.Features.Raster;

/// <summary>
/// PostGIS operation and semantic-variant inventory for durable raster GP discovery. This matrix
/// deliberately does not register an executor: serving SQL is evidence of a useful primitive, not
/// evidence of reference-output durability or semantic conformance.
/// </summary>
public static class PostgisRasterOperationCapabilityMatrix
{
    /// <summary>Provider baseline exercised by the RAST-016 PostGIS runtime matrix.</summary>
    public const string MinimumPostgisVersion = "3.4.0";

    /// <summary>Minimum independently packaged raster extension baseline.</summary>
    public const string MinimumPostgisRasterExtensionVersion = "3.4.0";

    private const string ProviderId = "postgis";
    private const string SemanticVersion = "1.0.0";
    private const string PolicyVersion = "postgis-raster-v1";

    private static readonly ReadOnlyCollection<RasterProviderExtensionRequirement> _rasterExtension =
        Array.AsReadOnly(new[]
        {
            new RasterProviderExtensionRequirement
            {
                ExtensionName = "postgis_raster",
                MinimumVersion = MinimumPostgisRasterExtensionVersion,
            },
        });

    private static readonly ReadOnlyCollection<RasterProviderOperationCapabilityRow> _rows =
        Array.AsReadOnly(BuildRows());

    /// <summary>
    /// Immutable per-operation/per-semantic-variant PostGIS inventory. Empty fixture collections
    /// are intentional RAST-016 proof gaps and remain fail-closed.
    /// </summary>
    public static IReadOnlyList<RasterProviderOperationCapabilityRow> Rows => _rows;

    /// <summary>Evaluates the inventory without mutating DI or planner registration.</summary>
    public static IReadOnlyList<RasterProviderCapabilityDiscovery> Discover(
        RasterProviderRuntimeSnapshot runtime,
        IEnumerable<RasterProviderExecutableSemanticVariant> executors,
        IEnumerable<RasterProviderSemanticProof> proofs)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return RasterProviderCapabilityMatrix.Discover(Rows, [runtime], executors, proofs);
    }

    private static RasterProviderOperationCapabilityRow[] BuildRows()
    {
        var rows = new List<RasterProviderOperationCapabilityRow>();

        Add(rows, "raster.clip", "default", RasterServingPrimitiveStatus.HonuaServingPath,
            ["ST_Clip"],
            "Parameterized geometry clipping exists in export, rendering, statistics, and mosaic serving paths; durable reference materialization is absent.");
        Add(rows, "raster.clip", "pixel-center", RasterServingPrimitiveStatus.HonuaServingPath,
            ["ST_Clip"],
            "Serving clip exists, but the pixel-center boundary contract has no durable reference-output route.",
            "clip.pixel-center-boundary.v1");

        AddReproject(rows, "default", RasterServingPrimitiveStatus.HonuaServingPath);
        AddReproject(rows, "nearest", RasterServingPrimitiveStatus.HonuaServingPath, "reproject.nearest-grid.v1");
        AddReproject(rows, "bilinear", RasterServingPrimitiveStatus.ProviderLibraryOnly);
        AddReproject(rows, "cubic", RasterServingPrimitiveStatus.ProviderLibraryOnly);
        AddReproject(rows, "cubicspline", RasterServingPrimitiveStatus.ProviderLibraryOnly);
        AddReproject(rows, "lanczos", RasterServingPrimitiveStatus.ProviderLibraryOnly);
        AddReproject(rows, "antimeridian", RasterServingPrimitiveStatus.ProviderLibraryOnly, "reproject.antimeridian.v1");
        AddReproject(rows, "invalid-crs", RasterServingPrimitiveStatus.ProviderLibraryOnly, "reproject.invalid-crs.v1");

        AddResample(rows, "default", RasterServingPrimitiveStatus.HonuaServingPath);
        AddResample(rows, "nearest", RasterServingPrimitiveStatus.HonuaServingPath);
        AddResample(rows, "bilinear", RasterServingPrimitiveStatus.HonuaServingPath, "resample.bilinear-nodata-edge.v1");
        AddResample(rows, "cubic", RasterServingPrimitiveStatus.HonuaServingPath);
        AddResample(rows, "cubicspline", RasterServingPrimitiveStatus.ProviderLibraryOnly);
        AddResample(rows, "lanczos", RasterServingPrimitiveStatus.HonuaServingPath);

        AddMosaic(rows, "default");
        AddMosaic(rows, "first");
        AddMosaic(rows, "last", "mosaic.last-overlap-nodata.v1");
        AddMosaic(rows, "min");
        AddMosaic(rows, "max");
        AddMosaic(rows, "mean");
        AddMosaic(rows, "cancellation", "mosaic.cancellation.v1");

        AddMapAlgebra(rows, "default");
        AddMapAlgebra(rows, "allowlisted-expression");
        AddMapAlgebra(rows, "a-plus-b", "map-algebra.nodata-propagation.v1");
        AddMapAlgebra(rows, "multiband-promotion", "multiband.promotion-color.v1");

        Add(rows, "raster.reclassify", "default", RasterServingPrimitiveStatus.ProviderLibraryOnly,
            ["ST_Reclass"],
            "PostGIS can reclassify cells, but Honua only has display colormap/stretch transforms; no closed canonical class model exists.");
        Add(rows, "raster.reclassify", "closed-open", RasterServingPrimitiveStatus.ProviderLibraryOnly,
            ["ST_Reclass"],
            "The provider primitive exists, but interval boundaries, pixel type, and NoData are not modeled by a durable Honua executor.",
            "reclassify.closed-open-boundaries.v1");

        AddSpectral(rows, "default", RasterServingPrimitiveStatus.HonuaServingPath);
        AddSpectral(rows, "ndvi", RasterServingPrimitiveStatus.HonuaServingPath, "spectral-index.ndvi-zero-denominator.v1");
        AddSpectral(rows, "ndwi", RasterServingPrimitiveStatus.HonuaServingPath);
        AddSpectral(rows, "ndbi", RasterServingPrimitiveStatus.ProviderLibraryOnly);
        AddSpectral(rows, "evi", RasterServingPrimitiveStatus.ProviderLibraryOnly);
        AddSpectral(rows, "savi", RasterServingPrimitiveStatus.HonuaServingPath);

        Add(rows, "raster.statistics", "default", RasterServingPrimitiveStatus.HonuaServingPath,
            ["ST_SummaryStats", "ST_SummaryStatsAgg"],
            "Whole-raster, mosaic, clipped, and rendered serving statistics exist; the canonical bounded GP artifact contract does not.");
        Add(rows, "raster.statistics", "population", RasterServingPrimitiveStatus.HonuaServingPath,
            ["ST_SummaryStats", "ST_SummaryStatsAgg"],
            "Serving statistics exist, but population/NoData semantics still require an exact durable provider proof.",
            "statistics.nodata-population.v1");
        Add(rows, "raster.statistics", "empty-input", RasterServingPrimitiveStatus.HonuaServingPath,
            ["ST_SummaryStats", "ST_SummaryStatsAgg"],
            "Serving statistics exist, but empty-input behavior is not exposed through a durable reference-output executor.",
            "statistics.empty-input.v1");

        Add(rows, "raster.histogram", "default", RasterServingPrimitiveStatus.HonuaServingPath,
            ["ST_Histogram"],
            "Bounded whole, mosaic, clipped, and rendered serving histograms exist; the canonical GP artifact schema does not.");
        Add(rows, "raster.histogram", "equal-width", RasterServingPrimitiveStatus.HonuaServingPath,
            ["ST_Histogram"],
            "Serving histogram bins exist, but exact bin-edge/count semantics need a durable executor and provider proof.",
            "histogram.bin-boundaries.v1");

        AddSurface(rows, "surface.roughness", "default", "ST_Roughness");
        AddSurface(rows, "surface.roughness", "three-by-three", "ST_Roughness", "surface.roughness-three-by-three.v1");
        AddSurface(rows, "surface.rugosity-tri", "default", "ST_TRI");
        AddSurface(rows, "surface.rugosity-tri", "three-by-three", "ST_TRI", "surface.rugosity-tri-three-by-three.v1");
        AddSurface(rows, "surface.rugosity-tpi", "default", "ST_TPI");
        AddSurface(rows, "surface.rugosity-tpi", "three-by-three", "ST_TPI", "surface.rugosity-tpi-three-by-three.v1");

        return rows.ToArray();
    }

    private static void AddReproject(
        ICollection<RasterProviderOperationCapabilityRow> rows,
        string variant,
        RasterServingPrimitiveStatus status,
        params string[] fixtures) => Add(
            rows,
            "raster.reproject",
            variant,
            status,
            ["ST_Transform"],
            status == RasterServingPrimitiveStatus.HonuaServingPath
                ? "Serving reprojection exists with default nearest-neighbor behavior; durable grid/origin/NoData materialization is absent."
                : "PostGIS accepts this reprojection semantic, but Honua serving does not expose the exact variant and no durable executor exists.",
            fixtures);

    private static void AddResample(
        ICollection<RasterProviderOperationCapabilityRow> rows,
        string variant,
        RasterServingPrimitiveStatus status,
        params string[] fixtures) => Add(
            rows,
            "raster.resample",
            variant,
            status,
            ["ST_Resize", "ST_Rescale", "ST_Resample"],
            status == RasterServingPrimitiveStatus.HonuaServingPath
                ? "Serving width/height, pixel-size, and reference-grid resampling exists; durable variant/output semantics are absent."
                : "PostGIS supports the kernel, but the current Honua serving allowlist does not expose it and no durable executor exists.",
            fixtures);

    private static void AddMosaic(
        ICollection<RasterProviderOperationCapabilityRow> rows,
        string variant,
        params string[] fixtures) => Add(
            rows,
            "raster.mosaic",
            variant,
            RasterServingPrimitiveStatus.HonuaServingPath,
            ["ST_Union"],
            "Ordered serving mosaics exist, but durable staged materialization, cleanup, and immutable reference publication do not.",
            fixtures);

    private static void AddMapAlgebra(
        ICollection<RasterProviderOperationCapabilityRow> rows,
        string variant,
        params string[] fixtures) => Add(
            rows,
            "raster.map-algebra",
            variant,
            RasterServingPrimitiveStatus.ProviderLibraryOnly,
            ["ST_MapAlgebra"],
            "Only internal hardcoded serving formulas exist; a closed allowlisted AST and durable executor are required before GP admission.",
            fixtures);

    private static void AddSpectral(
        ICollection<RasterProviderOperationCapabilityRow> rows,
        string variant,
        RasterServingPrimitiveStatus status,
        params string[] fixtures) => Add(
            rows,
            "raster.spectral-index",
            variant,
            status,
            ["ST_MapAlgebra"],
            status == RasterServingPrimitiveStatus.HonuaServingPath
                ? "An injection-safe hardcoded serving formula exists, but no durable reference-output spectral executor exists."
                : "PostGIS map algebra can express the index, but Honua has no hardcoded formula or admitted semantic for this variant.",
            fixtures);

    private static void AddSurface(
        ICollection<RasterProviderOperationCapabilityRow> rows,
        string processId,
        string variant,
        string primitive,
        params string[] fixtures) => Add(
            rows,
            processId,
            variant,
            RasterServingPrimitiveStatus.HonuaServingPath,
            [primitive],
            "PostGIS surface persistence exists; durable GP executor ownership remains delegated to RAST-011 and is not duplicated here.",
            fixtures);

    private static void Add(
        ICollection<RasterProviderOperationCapabilityRow> rows,
        string processId,
        string semanticVariantId,
        RasterServingPrimitiveStatus servingStatus,
        string[] primitives,
        string notes,
        params string[] fixtures) => rows.Add(new RasterProviderOperationCapabilityRow
        {
            ProviderId = ProviderId,
            Engine = RasterEngine.Postgis,
            ProcessId = processId,
            SemanticVersion = SemanticVersion,
            SemanticVariantId = semanticVariantId,
            ImplementationVersion = $"honua.postgis.{processId}@{SemanticVersion}",
            PolicyVersion = PolicyVersion,
            ServingPrimitiveStatus = servingStatus,
            ServingPrimitives = Array.AsReadOnly(primitives),
            ServingPrimitiveNotes = notes,
            MinimumRuntimeVersion = MinimumPostgisVersion,
            RequiredExtensions = _rasterExtension,
            RequiredFixtureIds = Array.AsReadOnly(fixtures),
        });
}
