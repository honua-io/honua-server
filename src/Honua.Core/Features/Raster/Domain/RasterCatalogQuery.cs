// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Raster.Domain;

// The message contains no SQL, connection string, or path, so it can be returned to the client.
public sealed class RasterCatalogValidationException : Exception
{
    public RasterCatalogValidationException(string message)
        : base(message)
    {
    }
}

public enum RasterCatalogSpatialRelation
{
    EnvelopeIntersects = 0,

    Intersects,

    Contains,

    Within,

    Overlaps,
}

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

    public double XMin { get; }

    public double YMin { get; }

    public double XMax { get; }

    public double YMax { get; }

    public int? Srid { get; }

    public byte[]? Geometry { get; }

    public RasterCatalogSpatialRelation Relation { get; }

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

public sealed record RasterCatalogQuery
{
    public RasterCatalogSpatialPredicate? SpatialPredicate { get; init; }

    public IReadOnlyList<long>? ObjectIds { get; init; }

    public DateTimeOffset? Timestamp { get; init; }

    // When no acquisition falls inside the window, nothing matches.
    public DateTimeOffset? TimeStart { get; init; }

    public int Offset { get; init; }

    public int? Limit { get; init; }

    public bool IncludeTotalCount { get; init; }

    public bool IncludeAggregateExtent { get; init; }

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

public sealed record RasterCatalogPage
{
    public required IReadOnlyList<RasterInfo> Rasters { get; init; }

    public required long TotalCount { get; init; }

    public RasterExtent? AggregateExtent { get; init; }

    public long RowsScanned { get; init; }

    public long RowsReturned { get; init; }

    public TimeSpan ProviderDuration { get; init; }

    public bool PredicatePushedDown { get; init; }
}
