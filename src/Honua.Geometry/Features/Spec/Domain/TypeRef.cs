// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;

namespace Honua.Core.Features.Spec.Domain;

#pragma warning disable CA1720 // Enum member names intentionally mirror user-facing spec type keywords.
public enum SpecTypeKind
{
    Unknown,

    String,

    Number,

    Integer,

    Boolean,

    Duration,

    Distance,

    Area,

    Geometry,

    DateTime,

    TimeRange,

    Crs,

    Dataset,

    Raster,

    Region,

    Feature,

    TileSource
}
#pragma warning restore CA1720

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

    public static TypeRef Intrinsic(SpecTypeKind kind)
        => _intrinsic.TryGetValue(kind, out var type) ? type : new TypeRef(kind);

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
                var found = ElementTypes.Any(candidate => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase));
                if (!found)
                {
                    return false;
                }
            }
        }

        return true;
    }

    public override string ToString()
    {
        return (Kind is SpecTypeKind.Dataset or SpecTypeKind.Raster) && ElementTypes is { Count: > 0 }
            ? $"{Kind.ToString().ToLowerInvariant()}<{string.Join(',', ElementTypes)}>"
            : Kind.ToString().ToLowerInvariant();
    }
}
