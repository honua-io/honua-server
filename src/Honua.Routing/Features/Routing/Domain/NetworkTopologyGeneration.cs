// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

public enum NetworkTopologyGenerationState
{
    Draft,

    Dirty,

    Building,

    Ready,

    Active,

    Failed,

    Retired,
}

public sealed record NetworkTopologyGeneration(
    string DatasetId,
    long Generation,
    long SourceRevision,
    NetworkTopologyGenerationState State,
    long RowVersion,
    int Srid,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ActivatedAt,
    string? FailureCode)
{
    public string? EdgeTable { get; init; }

    public string? VertexTable { get; init; }
}

public enum NetworkTopologyTransitionFailure
{
    None,

    StaleRowVersion,

    InvalidTransition,

    InvalidFailureCode,
}
