// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.TileOperations;

internal sealed record TileOperationStartRequest
{
    public required string Operation { get; init; }
    public string? ServiceId { get; init; }
    public int? LayerId { get; init; }
    public int? MinZoom { get; init; }
    public int? MaxZoom { get; init; }
    public string? TileMatrixSetId { get; init; }
    public double[]? Bbox { get; init; }
    public int? MaxTiles { get; init; }

    public string? Style { get; init; }

    public string? Format { get; init; }

    // A retry forwards the generation id so the run resumes rather than starting over.
    public string? GenerationId { get; init; }
}
