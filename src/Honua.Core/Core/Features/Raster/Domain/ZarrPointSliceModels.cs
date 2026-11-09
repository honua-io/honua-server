// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

public sealed record ZarrPointSliceSelection(string? Variable, string Dimension, double Coordinate);

public sealed record ZarrPointSliceReadRequest(
    double X,
    double Y,
    int? InputSrid,
    IReadOnlyList<ZarrPointSliceSelection> Selections);

public enum ZarrPointSliceReadStatus
{
    Success,

    RegistrationNotFound,

    ReaderUnavailable,

    InvalidSelection,

    OutsideCoverage,

    ReadFailed,
}

public readonly record struct ZarrPointSliceReadResult(
    ZarrPointSliceReadStatus Status,
    double? Value,
    string? Variable,
    string? Error);
