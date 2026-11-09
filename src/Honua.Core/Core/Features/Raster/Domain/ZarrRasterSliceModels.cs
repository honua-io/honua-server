// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

public sealed record ZarrRasterSliceReadRequest(
    RasterExtent? Bounds,
    int? InputSrid,
    int OutputWidth,
    int OutputHeight,
    IReadOnlyList<ZarrPointSliceSelection> Selections,
    int? OutputSrid = null,
    ResamplingAlgorithm Resampling = ResamplingAlgorithm.NearestNeighbor,
    RasterStretch? Stretch = null,
    RasterColormap? Colormap = null);

public enum ZarrRasterSliceReadStatus
{
    Success,

    RegistrationNotFound,

    ReaderUnavailable,

    InvalidSelection,

    OutsideCoverage,

    ReadFailed,
}

public readonly record struct ZarrRasterSliceReadResult(
    ZarrRasterSliceReadStatus Status,
    RasterResult? Raster,
    string? Variable,
    int DimensionCount,
    string? Error,
    byte[]? Rgba = null);
