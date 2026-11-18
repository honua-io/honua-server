// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Protocols.Ogc.Api.Coverages.Services;

internal enum ZarrCoverageFormat
{
    CoverageJson,
    GeoTiff
}

internal enum ZarrSpatialAxis
{
    None,
    X,
    Y
}

internal sealed record ZarrFormatChoice(ZarrCoverageFormat Format, IResult? Error)
{
    public static readonly ZarrFormatChoice CoverageJson = new(ZarrCoverageFormat.CoverageJson, null);

    public static readonly ZarrFormatChoice GeoTiff = new(ZarrCoverageFormat.GeoTiff, null);
}

internal sealed record ZarrCoverageTarget(string CollectionId, ZarrRegistration Registration);

internal sealed record ZarrSubsetRead(ZarrSubsetResult? Result, IResult? Error);

internal sealed record ZarrGeoTiffCoverageRequest
{
    public string? Variable { get; init; }

    public (double Low, double High)? X { get; init; }

    public (double Low, double High)? Y { get; init; }

    public (int Width, int Height)? ScaleSize { get; init; }

    public (DateTimeOffset? Start, DateTimeOffset? End)? DateTime { get; init; }

    public int? SliceIndex { get; init; }
}
