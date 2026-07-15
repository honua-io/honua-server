// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Thrown when a <see cref="RasterCatalogQuery"/> or <see cref="RasterCatalogSpatialPredicate"/>
/// carries invalid spatial, CRS, or paging input. The message is deliberately free of any
/// storage detail (SQL, connection strings, paths) so it is safe to surface, translated, to
/// clients as a generic bad-request.
/// </summary>
public sealed class RasterCatalogValidationException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RasterCatalogValidationException"/> class.
    /// </summary>
    /// <param name="message">A storage-agnostic validation message.</param>
    public RasterCatalogValidationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Spatial relationship a <see cref="RasterCatalogSpatialPredicate"/> applies against raster
/// footprints. <see cref="EnvelopeIntersects"/> is the default bounding-box windowing test; the
/// remaining members request an <b>exact</b> geometry predicate (evaluated provider-side against the
/// footprint geometry) and require <see cref="RasterCatalogSpatialPredicate.Geometry"/> to be set.
/// </summary>
public enum RasterCatalogSpatialRelation
{
    /// <summary>Footprint bounding box overlaps the filter box (edge contact matches). Default.</summary>
    EnvelopeIntersects = 0,

    /// <summary>Footprint geometry intersects the exact filter geometry.</summary>
    Intersects,

    /// <summary>Footprint geometry contains the exact filter geometry.</summary>
    Contains,

    /// <summary>Footprint geometry lies within the exact filter geometry.</summary>
    Within,

    /// <summary>Footprint geometry overlaps the exact filter geometry.</summary>
    Overlaps,
}

/// <summary>
/// Validated spatial predicate applied against raster footprints by the neutral raster-catalog query
/// contract. The axis-aligned box (<see cref="XMin"/>..<see cref="YMax"/>) is always present and is
/// used as the index-eligible <b>envelope-intersects</b> pre-filter: a footprint qualifies when its
/// extent's bounding box overlaps this box (edge contact counts as a match), mirroring the Esri
/// <c>esriSpatialRelEnvelopeIntersects</c> semantics.
/// </summary>
/// <remarks>
/// <para>
/// When <see cref="Geometry"/> is supplied (WKB) and <see cref="Relation"/> is not
/// <see cref="RasterCatalogSpatialRelation.EnvelopeIntersects"/>, an <b>exact</b> predicate
/// (<c>ST_Intersects</c>/<c>ST_Contains</c>/<c>ST_Within</c>/<c>ST_Overlaps</c>) is additionally
/// applied against the footprint geometry provider-side, with the box acting as the index pre-filter.
/// This honours the repository's spatial-semantics rule: envelope/viewport <c>bbox</c> windowing uses
/// the envelope-only test, while explicit <c>Intersects</c>/<c>Contains</c> relations on non-envelope
/// geometries use the exact relationship. The in-memory reference fallback (no geometry engine)
/// conservatively evaluates the envelope-intersects box only, which is a superset of every exact
/// relation's match set.
/// </para>
/// <para>
/// When <see cref="Srid"/> differs from a footprint's SRID the implementation transforms the box (and
/// the exact geometry) into the footprint SRID (PostGIS via <c>ST_Transform</c>; the in-memory
/// fallback via the shared CRS pipeline) before the test, so the comparison always stays in a single
/// coordinate system.
/// </para>
/// </remarks>
public readonly record struct RasterCatalogSpatialPredicate
{
    private RasterCatalogSpatialPredicate(
        double xMin,
        double yMin,
        double xMax,
        double yMax,
        int? srid,
        byte[]? geometry,
        RasterCatalogSpatialRelation relation)
    {
        XMin = xMin;
        YMin = yMin;
        XMax = xMax;
        YMax = yMax;
        Srid = srid;
        Geometry = geometry;
        Relation = relation;
    }

    /// <summary>Minimum X of the filter box.</summary>
    public double XMin { get; }

    /// <summary>Minimum Y of the filter box.</summary>
    public double YMin { get; }

    /// <summary>Maximum X of the filter box.</summary>
    public double XMax { get; }

    /// <summary>Maximum Y of the filter box.</summary>
    public double YMax { get; }

    /// <summary>
    /// SRID the filter box is expressed in, or <see langword="null"/> when the caller wants the
    /// footprint's native SRID assumed (no cross-CRS transform).
    /// </summary>
    public int? Srid { get; }

    /// <summary>
    /// Optional exact filter geometry as WKB, expressed in <see cref="Srid"/>. When set together with
    /// a non-envelope <see cref="Relation"/>, providers apply an exact spatial predicate against the
    /// footprint geometry (with the box as the index pre-filter). <see langword="null"/> requests the
    /// envelope-only test.
    /// </summary>
    public byte[]? Geometry { get; }

    /// <summary>
    /// Spatial relationship to apply. <see cref="RasterCatalogSpatialRelation.EnvelopeIntersects"/>
    /// (the default) uses the box only; other values require <see cref="Geometry"/> and request an
    /// exact provider-side predicate.
    /// </summary>
    public RasterCatalogSpatialRelation Relation { get; }

    /// <summary>
    /// Creates a validated predicate, throwing <see cref="RasterCatalogValidationException"/> when
    /// the input is not a finite, well-ordered box or the SRID is negative.
    /// </summary>
    /// <param name="xMin">Minimum X.</param>
    /// <param name="yMin">Minimum Y.</param>
    /// <param name="xMax">Maximum X.</param>
    /// <param name="yMax">Maximum Y.</param>
    /// <param name="srid">Optional SRID; must be positive when supplied.</param>
    /// <param name="geometry">Optional exact filter geometry (WKB) in <paramref name="srid"/>.</param>
    /// <param name="relation">Spatial relationship to apply. Defaults to envelope-intersects.</param>
    /// <returns>The validated predicate.</returns>
    public static RasterCatalogSpatialPredicate Create(
        double xMin,
        double yMin,
        double xMax,
        double yMax,
        int? srid,
        byte[]? geometry = null,
        RasterCatalogSpatialRelation relation = RasterCatalogSpatialRelation.EnvelopeIntersects)
    {
        if (!TryCreate(xMin, yMin, xMax, yMax, srid, out var predicate, out var error, geometry, relation))
        {
            throw new RasterCatalogValidationException(error);
        }

        return predicate;
    }

    /// <summary>
    /// Attempts to create a validated predicate. Rejects non-finite coordinates (NaN/Infinity),
    /// inverted boxes (<c>xMin &gt; xMax</c> or <c>yMin &gt; yMax</c>), and non-positive SRIDs
    /// without throwing. A non-envelope <paramref name="relation"/> requires a non-null
    /// <paramref name="geometry"/>.
    /// </summary>
    /// <param name="xMin">Minimum X.</param>
    /// <param name="yMin">Minimum Y.</param>
    /// <param name="xMax">Maximum X.</param>
    /// <param name="yMax">Maximum Y.</param>
    /// <param name="srid">Optional SRID; must be positive when supplied.</param>
    /// <param name="predicate">The validated predicate on success.</param>
    /// <param name="error">A storage-agnostic error message on failure.</param>
    /// <param name="geometry">Optional exact filter geometry (WKB) in <paramref name="srid"/>.</param>
    /// <param name="relation">Spatial relationship to apply. Defaults to envelope-intersects.</param>
    /// <returns><see langword="true"/> when the input is valid; otherwise <see langword="false"/>.</returns>
    public static bool TryCreate(
        double xMin,
        double yMin,
        double xMax,
        double yMax,
        int? srid,
        out RasterCatalogSpatialPredicate predicate,
        out string error,
        byte[]? geometry = null,
        RasterCatalogSpatialRelation relation = RasterCatalogSpatialRelation.EnvelopeIntersects)
    {
        predicate = default;
        error = string.Empty;

        if (!IsFinite(xMin) || !IsFinite(yMin) || !IsFinite(xMax) || !IsFinite(yMax))
        {
            error = "Spatial filter envelope coordinates must be finite numbers.";
            return false;
        }

        if (xMin > xMax || yMin > yMax)
        {
            error = "Spatial filter envelope is inverted: min coordinates must not exceed max coordinates.";
            return false;
        }

        if (srid is { } value && value <= 0)
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"Spatial filter SRID must be a positive integer; got {value}.");
            return false;
        }

        if (relation != RasterCatalogSpatialRelation.EnvelopeIntersects && geometry is not { Length: > 0 })
        {
            error = "An exact spatial relationship requires a filter geometry.";
            return false;
        }

        predicate = new RasterCatalogSpatialPredicate(xMin, yMin, xMax, yMax, srid, geometry, relation);
        return true;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

/// <summary>
/// Neutral, provider-agnostic raster-catalog query. Carries the validated spatial predicate plus
/// the identity, temporal, and paging inputs a provider needs to filter and page a raster catalog
/// <b>before materialization</b>. Protocol-specific concerns (Esri <c>where</c> evaluation, computed
/// field ordering, field masking) are layered on top by the adapter and are intentionally absent
/// here so the contract stays storage-neutral.
/// </summary>
public sealed record RasterCatalogQuery
{
    /// <summary>
    /// Optional validated envelope-intersects predicate against footprint extents. <c>null</c>
    /// applies no spatial filter.
    /// </summary>
    public RasterCatalogSpatialPredicate? SpatialPredicate { get; init; }

    /// <summary>
    /// Optional identity filter. When set, only rasters whose id is in this set match. An empty
    /// list matches nothing.
    /// </summary>
    public IReadOnlyList<long>? ObjectIds { get; init; }

    /// <summary>
    /// Optional temporal instant applying "newest batch" snapshot semantics: only rasters whose
    /// effective acquisition (acquisition date, falling back to created-at) equals the single
    /// most-recent effective acquisition across the layer at or before this instant match. When no
    /// acquisition is at or before the instant, nothing matches.
    /// </summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>Zero-based paging offset. Ignored when <see cref="Limit"/> is <c>null</c>.</summary>
    public int Offset { get; init; }

    /// <summary>
    /// Maximum number of rows to return. <c>null</c> returns every matching row (no paging pushdown),
    /// used by the adapter when a post-read step it owns (e.g. Esri <c>where</c> or computed-field
    /// ordering) still needs the full spatially-filtered set.
    /// </summary>
    public int? Limit { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the provider computes the total matched-row count (pre-paging)
    /// so the adapter can report it without a second round trip.
    /// </summary>
    public bool IncludeTotalCount { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the provider computes the aggregate (union) extent of the
    /// matched set (pre-paging), in the catalog's native SRID.
    /// </summary>
    public bool IncludeAggregateExtent { get; init; }

    /// <summary>
    /// Validates the paging inputs, throwing <see cref="RasterCatalogValidationException"/> for a
    /// negative offset or a non-positive limit. Providers call this at entry so invalid input is
    /// rejected safely before any storage access.
    /// </summary>
    public void Validate()
    {
        if (Offset < 0)
        {
            throw new RasterCatalogValidationException("Paging offset must not be negative.");
        }

        if (Limit is { } limit && limit <= 0)
        {
            throw new RasterCatalogValidationException("Paging limit must be a positive integer when supplied.");
        }

        if (ObjectIds is { Count: > 10000 })
        {
            throw new RasterCatalogValidationException("Too many objectIds supplied in a single catalog query.");
        }
    }
}

/// <summary>
/// A page of raster-catalog rows returned by the neutral query contract, plus the pre-paging
/// aggregate metadata and the read-bounding telemetry counters the adapter surfaces (without
/// leaking any SQL).
/// </summary>
public sealed record RasterCatalogPage
{
    /// <summary>The materialized page of matching rasters, in the catalog's natural order.</summary>
    public required IReadOnlyList<RasterInfo> Rasters { get; init; }

    /// <summary>
    /// Total number of rows matching the predicate before paging. Equals <see cref="Rasters"/> count
    /// when <see cref="RasterCatalogQuery.IncludeTotalCount"/> was not requested.
    /// </summary>
    public required long TotalCount { get; init; }

    /// <summary>
    /// Aggregate (union) extent of the matched set in the catalog's native SRID, or <c>null</c> when
    /// not requested or when no matching row carries an extent.
    /// </summary>
    public RasterExtent? AggregateExtent { get; init; }

    /// <summary>
    /// Number of rows the predicate matched (the bound the storage read is limited to). For an
    /// indexed pushdown this is the count of index-qualified rows, not the whole catalog.
    /// </summary>
    public long RowsScanned { get; init; }

    /// <summary>Number of rows materialized into <see cref="Rasters"/> (the page size).</summary>
    public long RowsReturned { get; init; }

    /// <summary>
    /// Provider-side duration for the query, surfaced as telemetry. Never includes SQL text.
    /// </summary>
    public TimeSpan ProviderDuration { get; init; }

    /// <summary>
    /// <see langword="true"/> when the provider pushed the predicate and paging into storage;
    /// <see langword="false"/> when it fell back to a bounded in-memory materialization.
    /// </summary>
    public bool PredicatePushedDown { get; init; }
}
