// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Federation.Domain;

/// <summary>
/// Identifies a single canonical predicate that a federated query carries.
/// </summary>
public enum FederationPredicateKind
{
    /// <summary>Attribute filter (<c>WHERE</c> / parameterized SQL / object-id set).</summary>
    AttributeFilter,

    /// <summary>Spatial filter (envelope or richer spatial relationship).</summary>
    SpatialFilter,

    /// <summary>Temporal interval filter.</summary>
    TemporalFilter,

    /// <summary>Result ordering (<c>ORDER BY</c>).</summary>
    OrderBy,

    /// <summary>Result paging (offset/limit).</summary>
    Paging
}

/// <summary>
/// Records where a single predicate is evaluated in a federated query: pushed down to the
/// remote source, or evaluated locally by the federation layer after rows are fetched.
/// </summary>
/// <param name="Predicate">The predicate this decision applies to.</param>
/// <param name="PushedDown">
/// <see langword="true"/> when the remote source evaluates the predicate; otherwise the
/// federation layer evaluates it locally.
/// </param>
/// <param name="Reason">Human-readable explanation for the decision (diagnostics / Admin UI).</param>
public readonly record struct FederationPredicateDecision(
    FederationPredicateKind Predicate,
    bool PushedDown,
    string Reason);

/// <summary>
/// The plan produced for a single federated source: which predicates the remote source
/// evaluates, which the federation layer refines locally, and whether a local join step is
/// required to combine the remote rows with a local PostGIS layer.
/// </summary>
public sealed record FederationQueryPlan
{
    /// <summary>
    /// Gets the source this plan targets.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Gets the resolved capabilities used to build the plan.
    /// </summary>
    public required FederatedSourceCapabilities Capabilities { get; init; }

    /// <summary>
    /// Gets the per-predicate push-down decisions.
    /// </summary>
    public required ImmutableArray<FederationPredicateDecision> Decisions { get; init; }

    /// <summary>
    /// Gets a value indicating whether any predicate must be evaluated locally after the
    /// remote fetch. When <see langword="true"/> the federation layer over-fetches a
    /// candidate superset from the remote source and refines it locally.
    /// </summary>
    public bool RequiresLocalRefinement => Decisions.Any(static d => !d.PushedDown);

    /// <summary>
    /// Gets a value indicating whether the plan combines the remote source with a local
    /// PostGIS layer through a local join step.
    /// </summary>
    public required bool RequiresLocalJoin { get; init; }
}
