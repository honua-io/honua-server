// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Federation.Models;

/// <summary>
/// Admin response payload describing a configured federated source. Carries no transport
/// credentials.
/// </summary>
internal sealed record FederationSourceResponse
{
    /// <summary>Stable source identifier.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-readable display name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Transport family (HonuaGrpc, EsriRest, OgcWfs).</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Base endpoint URL of the remote service.</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>Remote layer/collection identifier exposed for federated joins.</summary>
    public string RemoteLayer { get; init; } = string.Empty;

    /// <summary>Per-request timeout applied to remote calls, in seconds.</summary>
    public int RequestTimeoutSeconds { get; init; }

    /// <summary>Whether the source is enabled for federation.</summary>
    public bool Enabled { get; init; }
}

/// <summary>
/// One predicate placement decision within a federation query plan.
/// </summary>
internal sealed record FederationPredicateDecisionResponse
{
    /// <summary>The predicate family (AttributeFilter, SpatialFilter, TemporalFilter, OrderBy, Paging).</summary>
    public string Predicate { get; init; } = string.Empty;

    /// <summary>Whether the predicate is evaluated by the remote source (true) or locally (false).</summary>
    public bool PushedDown { get; init; }

    /// <summary>Human-readable explanation for the decision.</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Admin response payload describing the plan for a sample query against a federated source.
/// </summary>
internal sealed record FederationQueryPlanResponse
{
    /// <summary>The source the plan targets.</summary>
    public string SourceId { get; init; } = string.Empty;

    /// <summary>The sample query <c>where</c> clause the plan was built for.</summary>
    public string? SampleWhere { get; init; }

    /// <summary>Whether a sample bbox spatial predicate was included.</summary>
    public bool SampleBbox { get; init; }

    /// <summary>Per-predicate push-down decisions.</summary>
    public IReadOnlyList<FederationPredicateDecisionResponse> Decisions { get; init; } =
        Array.Empty<FederationPredicateDecisionResponse>();

    /// <summary>Whether any predicate must be refined locally after the remote fetch.</summary>
    public bool RequiresLocalRefinement { get; init; }

    /// <summary>Whether the plan combines the remote source with a local PostGIS layer.</summary>
    public bool RequiresLocalJoin { get; init; }
}
