// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

public enum RasterMergeStrategy
{
    Newest = 0,

    Oldest = 1,

    Average = 2,

    Max = 3,

    Min = 4
}

public enum RasterMosaicOrdering
{
    AcquisitionNewest = 0,

    AcquisitionOldest = 1,

    Northwest = 2,

    LockOrder = 3,

    // The attribute column cannot live on the ordering enum, so the column and direction are carried separately.
    Attribute = 4,

    Seamline = 5,

    Nadir = 6
}

// The column is an allowlisted catalog column, never caller text, so it can be placed in the mosaic ORDER BY.
public readonly record struct RasterMosaicAttributeSort(string Column, bool Ascending);

public readonly record struct RasterSelectionQuery
{
    public byte[]? Geometry { get; init; }

    public int? GeometrySrid { get; init; }

    public DateTimeOffset? Timestamp { get; init; }

    public DateTimeOffset? TimeStart { get; init; }
}
