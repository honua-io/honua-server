// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;

namespace Honua.Core.Features.Spec.Domain;

/// <summary>
/// Enumeration of intrinsic type kinds. Collection/parametric types carry
/// their element metadata on the <see cref="TypeRef"/> wrapper rather than
/// appearing as separate enum members.
/// </summary>
#pragma warning disable CA1720 // Enum member names intentionally mirror user-facing spec type keywords.
public enum SpecTypeKind
{
    /// <summary>Sentinel for unresolved / error types.</summary>
    Unknown,

    /// <summary>UTF-8 string literal.</summary>
    String,

    /// <summary>IEEE-754 double-precision number.</summary>
    Number,

    /// <summary>Integer; modelled on <see cref="long"/>.</summary>
    Integer,

    /// <summary>Boolean true/false.</summary>
    Boolean,

    /// <summary>Duration literal (e.g. <c>30.s</c>, <c>5.min</c>).</summary>
    Duration,

    /// <summary>Distance literal (e.g. <c>5.km</c>). CRS-sensitive.</summary>
    Distance,

    /// <summary>Area literal (e.g. <c>10.ha</c>). CRS-sensitive.</summary>
    Area,

    /// <summary>Geometry literal, typically via NetTopologySuite.</summary>
    Geometry,

    /// <summary>ISO-8601 datetime.</summary>
    DateTime,

    /// <summary>ISO-8601 interval / time range.</summary>
    TimeRange,

    /// <summary>Coordinate reference system, e.g. <c>"EPSG:3857"</c>.</summary>
    Crs,

    /// <summary>Parametric dataset type — carries column metadata.</summary>
    Dataset,

    /// <summary>Parametric raster type — carries band metadata.</summary>
    Raster,

    /// <summary>Region (bounding region used by viewport).</summary>
    Region,

    /// <summary>Feature (single spatial record).</summary>
    Feature,

    /// <summary>Tile source (feature-service, XYZ, etc.).</summary>
    TileSource
}
#pragma warning restore CA1720

/// <summary>
/// Typed reference used by the type checker. Immutable value type with
/// optional parametric metadata (for dataset/raster).
/// </summary>
/// <param name="Kind">Discriminator.</param>
/// <param name="ElementTypes">Ordered column/band names for dataset/raster.</param>
/// <param name="Crs">Associated CRS when known (from catalog metadata or explicit annotation).</param>
public sealed record TypeRef(
    SpecTypeKind Kind,
    IReadOnlyList<string>? ElementTypes = null,
    string? Crs = null)
{
    private static readonly FrozenDictionary<SpecTypeKind, TypeRef> _intrinsic =
        new Dictionary<SpecTypeKind, TypeRef>
        {
            [SpecTypeKind.Unknown] = new(SpecTypeKind.Unknown),
            [SpecTypeKind.String] = new(SpecTypeKind.String),
            [SpecTypeKind.Number] = new(SpecTypeKind.Number),
            [SpecTypeKind.Integer] = new(SpecTypeKind.Integer),
            [SpecTypeKind.Boolean] = new(SpecTypeKind.Boolean),
            [SpecTypeKind.Duration] = new(SpecTypeKind.Duration),
            [SpecTypeKind.Distance] = new(SpecTypeKind.Distance),
            [SpecTypeKind.Area] = new(SpecTypeKind.Area),
            [SpecTypeKind.Geometry] = new(SpecTypeKind.Geometry),
            [SpecTypeKind.DateTime] = new(SpecTypeKind.DateTime),
            [SpecTypeKind.TimeRange] = new(SpecTypeKind.TimeRange),
            [SpecTypeKind.Crs] = new(SpecTypeKind.Crs),
            [SpecTypeKind.Region] = new(SpecTypeKind.Region),
            [SpecTypeKind.Feature] = new(SpecTypeKind.Feature),
            [SpecTypeKind.TileSource] = new(SpecTypeKind.TileSource)
        }.ToFrozenDictionary();

    /// <summary>
    /// Returns the canonical intrinsic <see cref="TypeRef"/> for the given kind
    /// (without parametric or CRS metadata). Useful for primitive literals.
    /// </summary>
    /// <param name="kind">Kind to look up.</param>
    /// <returns>Shared intrinsic instance.</returns>
    public static TypeRef Intrinsic(SpecTypeKind kind)
        => _intrinsic.TryGetValue(kind, out var type) ? type : new TypeRef(kind);

    /// <summary>
    /// Returns <c>true</c> when this type is assignable to <paramref name="other"/>
    /// using structural compatibility (dataset → dataset when element sets are
    /// either unspecified on the target or a superset on the source).
    /// </summary>
    /// <param name="other">Target type.</param>
    public bool IsAssignableTo(TypeRef other)
    {
        if (other is null)
        {
            return false;
        }

        if (other.Kind == SpecTypeKind.Unknown)
        {
            return true;
        }

        // CRS identifiers are carried as strings (e.g. "EPSG:3857"). A String
        // literal therefore satisfies a Crs-typed port without an explicit cast.
        if (other.Kind == SpecTypeKind.Crs && Kind == SpecTypeKind.String)
        {
            return true;
        }

        if (Kind != other.Kind)
        {
            return false;
        }

        if (Kind is SpecTypeKind.Dataset or SpecTypeKind.Raster)
        {
            if (other.ElementTypes is null || other.ElementTypes.Count == 0)
            {
                return true;
            }

            if (ElementTypes is null)
            {
                return false;
            }

            foreach (var name in other.ElementTypes)
            {
                var found = false;
                foreach (var candidate in ElementTypes)
                {
                    if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Human-readable rendering used in diagnostics (e.g. <c>dataset&lt;id,name&gt;</c>).
    /// </summary>
    public override string ToString()
    {
        return (Kind is SpecTypeKind.Dataset or SpecTypeKind.Raster) && ElementTypes is { Count: > 0 }
            ? $"{Kind.ToString().ToLowerInvariant()}<{string.Join(',', ElementTypes)}>"
            : Kind.ToString().ToLowerInvariant();
    }
}
