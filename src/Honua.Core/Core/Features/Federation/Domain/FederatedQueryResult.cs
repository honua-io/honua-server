// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Federation.Domain;

public sealed record FederatedQueryResult
{
    public required string SourceId { get; init; }

    public required FederationQueryPlan Plan { get; init; }

    public required QueryResult<Feature> Result { get; init; }

    public required TimeSpan Elapsed { get; init; }

    // When local-refinement families remain, the rows are an over-fetched superset rather than an exact result.
    public ImmutableArray<FederationPredicateKind> UnappliedLocalRefinements { get; init; } =
        ImmutableArray<FederationPredicateKind>.Empty;
}

public readonly record struct FederatedSourceFailure(string SourceId, FederatedSourceUnavailableReason Reason);

public sealed record FederatedCombinedResult
{
    public required ImmutableArray<FederatedQueryResult> Sources { get; init; }

    public required ImmutableArray<FederatedSourceFailure> Failures { get; init; }

    public required QueryResult<Feature> Combined { get; init; }
}
