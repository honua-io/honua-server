// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Bim;

public sealed record CityGmlModel
{
    public string? SourceCrs { get; init; }

    public bool LatitudeFirst { get; init; }

    public required IReadOnlyList<CityGmlBuilding> Buildings { get; init; }
}

public sealed record CityGmlBuilding
{
    public required string Id { get; init; }

    public string? Name { get; init; }

    public int? StoreysAboveGround { get; init; }

    public int? StoreysBelowGround { get; init; }

    public IReadOnlyDictionary<string, string> Attributes { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    public required IReadOnlyList<CityGmlStorey> Storeys { get; init; }
}

public sealed record CityGmlStorey
{
    public required string Id { get; init; }

    public string? Name { get; init; }

    public required IReadOnlyList<CityGmlBoundarySurface> Surfaces { get; init; }
}

public sealed record CityGmlBoundarySurface
{
    public required string Id { get; init; }

    public required string SurfaceType { get; init; }

    public required string Discipline { get; init; }

    public IReadOnlyDictionary<string, string> Attributes { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    public required IReadOnlyList<CityGmlVertex> Ring { get; init; }
}

public readonly record struct CityGmlVertex(double X, double Y, double Z);
