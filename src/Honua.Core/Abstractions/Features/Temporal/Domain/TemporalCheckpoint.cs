// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Temporal.Domain;

public enum TemporalCheckpointKind
{
    Generation = 0,

    Timestamp = 1,

    Transaction = 2,

    Release = 3,

    Job = 4,

    EditSession = 5,

    Named = 6
}

public sealed record TemporalCheckpoint(
    TemporalCheckpointKind Kind,
    string? Value = null,
    long? Generation = null)
{
    public static TemporalCheckpoint ForGeneration(long generation)
        => new(TemporalCheckpointKind.Generation, Value: null, Generation: generation);
}

public sealed record ResolvedTemporalCheckpoint(
    TemporalCheckpoint Requested,
    long Generation);
