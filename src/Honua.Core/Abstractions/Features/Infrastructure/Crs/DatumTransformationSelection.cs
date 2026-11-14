// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

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

    public bool RequiresCoordinateEpoch { get; init; }

    public double? CoordinateEpoch { get; init; }

    public DatumTransformationSelection WithCoordinateEpoch(double? coordinateEpoch)
    {
        if (coordinateEpoch is { } epoch && !double.IsFinite(epoch))
        {
            throw new ArgumentException("coordinateEpoch must be a finite decimal year.", nameof(coordinateEpoch));
        }

        if (RequiresCoordinateEpoch && coordinateEpoch is null)
        {
            throw new ArgumentException("This datum transformation requires coordinateEpoch in decimal years.", nameof(coordinateEpoch));
        }

        if (CoordinateEpoch is { } boundEpoch && coordinateEpoch != boundEpoch)
        {
            throw new ArgumentException("A bound datum transformation cannot change coordinateEpoch.", nameof(coordinateEpoch));
        }

        return this with
        {
            CoordinateEpoch = coordinateEpoch,
            ProjPipeline = coordinateEpoch is { } observationEpoch
                ? ProjPipeline?.Replace("{coordinateEpoch}", observationEpoch.ToString("R", CultureInfo.InvariantCulture), StringComparison.Ordinal)
                : ProjPipeline
        };
    }

    public bool TransformForward { get; init; } = true;

    // A missing grid must fail rather than fall back to a Helmert approximation.
    public IReadOnlyList<string> RequiredGrids { get; init; } = [];
}
