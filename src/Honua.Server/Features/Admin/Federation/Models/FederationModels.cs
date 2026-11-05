// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Federation.Models;

internal sealed record FederationSourceResponse
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public string Endpoint { get; init; } = string.Empty;

    public string RemoteLayer { get; init; } = string.Empty;

    public int RequestTimeoutSeconds { get; init; }

    public bool Enabled { get; init; }
}

internal sealed record FederationPredicateDecisionResponse
{
    public string Predicate { get; init; } = string.Empty;

    public bool PushedDown { get; init; }

    public string Reason { get; init; } = string.Empty;
}

internal sealed record FederationQueryPlanResponse
{
    public string SourceId { get; init; } = string.Empty;

    public string? SampleWhere { get; init; }

    public bool SampleBbox { get; init; }

    public IReadOnlyList<FederationPredicateDecisionResponse> Decisions { get; init; } =
        Array.Empty<FederationPredicateDecisionResponse>();

    public bool RequiresLocalRefinement { get; init; }

    public bool RequiresLocalJoin { get; init; }
}
