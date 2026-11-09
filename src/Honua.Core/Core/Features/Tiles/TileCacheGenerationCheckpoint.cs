// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Tiles;

public sealed record TileCacheGenerationCheckpoint
{
    public required string GenerationId { get; init; }

    public required string Operation { get; init; }

    public int CompletedMetatileBlocks { get; init; }

    public long CompletedUnitCount { get; init; }

    public long FailedUnitCount { get; init; }

    // Failed unit keys are truncated to a fixed bound on write so the checkpoint cannot grow without limit.
    public required IReadOnlyList<string> FailedUnits { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }

    public int Attempt { get; init; } = 1;
}

public static class TileCacheGenerationCheckpointBounds
{
    public const int MaxFailedUnits = 1_000;

    public const int MaxFailedUnitLength = 1_024;

    public static TileCacheGenerationCheckpoint Sanitize(TileCacheGenerationCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.GenerationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.Operation);

        var boundedFailedUnits = (checkpoint.FailedUnits ?? [])
            .Where(static unit => !string.IsNullOrWhiteSpace(unit))
            .Select(static unit => unit.Length > MaxFailedUnitLength ? unit[..MaxFailedUnitLength] : unit)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxFailedUnits)
            .ToArray();

        return checkpoint with
        {
            GenerationId = checkpoint.GenerationId.Trim(),
            Operation = checkpoint.Operation.Trim(),
            CompletedMetatileBlocks = checkpoint.CompletedMetatileBlocks < 0 ? 0 : checkpoint.CompletedMetatileBlocks,
            CompletedUnitCount = checkpoint.CompletedUnitCount < 0 ? 0 : checkpoint.CompletedUnitCount,
            FailedUnitCount = checkpoint.FailedUnitCount < 0 ? 0 : checkpoint.FailedUnitCount,
            FailedUnits = boundedFailedUnits,
            Attempt = checkpoint.Attempt < 1 ? 1 : checkpoint.Attempt
        };
    }
}

[JsonSerializable(typeof(TileCacheGenerationCheckpoint))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class TileCacheGenerationCheckpointJsonContext : JsonSerializerContext
{
}
