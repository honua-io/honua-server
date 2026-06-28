// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Federation.Domain;

/// <summary>
/// The outcome of executing a federated query against a single source: the combined rows
/// (remote fetch plus local refinement), the plan that produced them, and lightweight
/// per-call diagnostics. The diagnostics are a deliberate precursor to the per-source latency
/// and error metrics tracked in issue #341.
/// </summary>
public sealed record FederatedQueryResult
{
    /// <summary>
    /// Gets the identifier of the source this result came from.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Gets the plan that drove push-down vs local refinement for this source.
    /// </summary>
    public required FederationQueryPlan Plan { get; init; }

    /// <summary>
    /// Gets the rows after the remote fetch and local refinement have been combined.
    /// </summary>
    public required QueryResult<Feature> Result { get; init; }

    /// <summary>
    /// Gets the wall-clock time spent on the remote fetch (including resilience handling).
    /// </summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Gets the local-refinement predicate families the plan required but the executor could
    /// not yet apply (for example exact spatial-relationship refinement, which is deferred to
    /// a later increment). When non-empty, the rows are an over-fetched superset for those
    /// predicates rather than an exact result, so callers can decide how to surface them.
    /// </summary>
    public ImmutableArray<FederationPredicateKind> UnappliedLocalRefinements { get; init; } =
        ImmutableArray<FederationPredicateKind>.Empty;
}

/// <summary>
/// Records a source that failed during a multi-source federated query.
/// </summary>
/// <param name="SourceId">The identifier of the failed source.</param>
/// <param name="Reason">Why the source was unavailable.</param>
public readonly record struct FederatedSourceFailure(string SourceId, FederatedSourceUnavailableReason Reason);

/// <summary>
/// The outcome of executing a federated query across multiple sources. Successful sources are
/// merged into a single <see cref="Combined"/> result; unavailable sources are recorded in
/// <see cref="Failures"/> rather than failing the whole request, so a single failing source
/// cannot cascade into a total outage.
/// </summary>
public sealed record FederatedCombinedResult
{
    /// <summary>
    /// Gets the per-source results that completed successfully.
    /// </summary>
    public required ImmutableArray<FederatedQueryResult> Sources { get; init; }

    /// <summary>
    /// Gets the sources that were unavailable for this query.
    /// </summary>
    public required ImmutableArray<FederatedSourceFailure> Failures { get; init; }

    /// <summary>
    /// Gets the merged rows across all successful sources, after any cross-source ordering and
    /// paging have been applied locally.
    /// </summary>
    public required QueryResult<Feature> Combined { get; init; }
}
