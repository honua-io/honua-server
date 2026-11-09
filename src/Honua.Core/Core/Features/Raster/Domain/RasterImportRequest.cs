// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

public sealed record RasterImportRequest
{
    public required int LayerId { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string FilePath { get; init; }

    public required string FileName { get; init; }

    public required SupportedRasterFormat Format { get; init; }

    public int? Srid { get; init; }

    public string? WorldFileContent { get; init; }

    public string? PrjFileContent { get; init; }

    public DateTimeOffset? AcquisitionDate { get; init; }

    public int[] TileZoomLevels { get; init; } = [0, 1, 2, 3, 4, 5, 6, 7, 8];

    // A default import does not build overview pyramids, because those levels have no native-source lineage and nothing reads them.
    public int[] OverviewFactors { get; init; } = [];
}
