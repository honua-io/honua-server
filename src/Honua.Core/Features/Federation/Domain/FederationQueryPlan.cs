// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Federation.Domain;

public enum FederationPredicateKind
{
    AttributeFilter,

    SpatialFilter,

    TemporalFilter,

    OrderBy,

    Paging
}

public readonly record struct FederationPredicateDecision(
    FederationPredicateKind Predicate,
    bool PushedDown,
    string Reason);

public sealed record FederationQueryPlan
{
    public required string SourceId { get; init; }

    public required FederatedSourceCapabilities Capabilities { get; init; }

    public required ImmutableArray<FederationPredicateDecision> Decisions { get; init; }

    public bool RequiresLocalRefinement => Decisions.Any(static d => !d.PushedDown);

    public required bool RequiresLocalJoin { get; init; }
}
