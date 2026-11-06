// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Crs;

// The pipeline is chosen explicitly because PROJ's default ST_Transform does not match Esri's published geotransformation.
public sealed record DatumTransformationSelection
{
    public int? Wkid { get; init; }

    public required string Name { get; init; }

    public required int FromSrid { get; init; }

    public required int ToSrid { get; init; }

    public int? EpsgOperationCode { get; init; }

    // An operation pipeline must go to ST_TransformPipeline because the text overload of ST_Transform accepts a CRS definition.
    public string? ProjPipeline { get; init; }

    public bool TransformForward { get; init; } = true;

    // A missing grid must fail rather than fall back to a Helmert approximation.
    public IReadOnlyList<string> RequiredGrids { get; init; } = [];
}
