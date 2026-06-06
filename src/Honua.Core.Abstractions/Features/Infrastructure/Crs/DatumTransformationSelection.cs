// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Crs;

/// <summary>
/// A resolved datum (geographic/geodetic) transformation selected for a single
/// source-to-target reprojection. Carries enough provenance to drive an
/// authoritative PROJ pipeline through PostGIS' 3-argument
/// <c>ST_Transform(geom, '&lt;pipeline&gt;', toSrid)</c> overload and to surface
/// the choice back to clients (matching ArcGIS' geotransformation metadata).
/// </summary>
/// <remarks>
/// <para>
/// Honua keeps PostGIS/PROJ as the transformation engine; the gap addressed by
/// this type is <em>pipeline selection</em>, not engine capability. The default
/// 2-argument <c>ST_Transform</c> lets PROJ pick its own "best available"
/// pipeline, which does not always match Esri's published default
/// geotransformation table (the divergence reaches ~1–2&#160;m for NAD83↔WGS84
/// and more for NAD27). Selecting the pipeline explicitly closes that gap.
/// </para>
/// <para>
/// Selections are sourced from an auditable catalog
/// (<see cref="IDatumTransformationCatalog"/>) rather than hard-coded so the
/// Esri-parity table can be reviewed and extended as data.
/// </para>
/// </remarks>
public sealed record DatumTransformationSelection
{
    /// <summary>
    /// The Esri/ArcGIS Well-Known ID (WKID) of the geotransformation, when known.
    /// This is the value ArcGIS clients pass in <c>datumTransformation</c> and the
    /// value advertised back in service metadata. May be <see langword="null"/>
    /// when the selection is expressed only as a PROJ pipeline.
    /// </summary>
    public int? Wkid { get; init; }

    /// <summary>
    /// Human-readable transformation name (for example
    /// <c>NAD_1983_To_WGS_1984_1</c>) used for diagnostics and metadata.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The source geographic CRS SRID this transformation departs from.
    /// </summary>
    public required int FromSrid { get; init; }

    /// <summary>
    /// The target geographic CRS SRID this transformation arrives at.
    /// </summary>
    public required int ToSrid { get; init; }

    /// <summary>
    /// The EPSG coordinate-operation code backing this transformation, when the
    /// pipeline maps onto a published EPSG operation. Used as a test oracle and
    /// for provenance. May be <see langword="null"/> for composite/custom pipelines.
    /// </summary>
    public int? EpsgOperationCode { get; init; }

    /// <summary>
    /// The explicit PROJ pipeline string handed to PostGIS' 3-argument
    /// <c>ST_Transform</c>. When <see langword="null"/>, callers should fall back
    /// to the SRID-only PostGIS pipeline (2-argument <c>ST_Transform</c>) — that
    /// situation is only valid for identity/no-shift pairs.
    /// </summary>
    public string? ProjPipeline { get; init; }

    /// <summary>
    /// Whether the transformation is to be applied in its forward direction
    /// (source → target). When <see langword="false"/> the inverse is requested.
    /// Mirrors the ArcGIS <c>transformForward</c> flag.
    /// </summary>
    public bool TransformForward { get; init; } = true;

    /// <summary>
    /// Names of PROJ grid-shift files (NTv2/NADCON/GEOID, etc.) the pipeline
    /// depends on. When non-empty the runtime must verify the grids are present
    /// in the PROJ data path before applying the pipeline; a missing grid must
    /// fail explicitly rather than silently degrading to a Helmert approximation.
    /// </summary>
    public IReadOnlyList<string> RequiredGrids { get; init; } = [];
}
