// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Grounding.Domain;

/// <summary>
/// A single ranked candidate surfaced by the grounding service. The score is
/// deterministic for a given engine + catalog snapshot; the confidence band is
/// derived from the configured grounding thresholds.
/// </summary>
public sealed record GroundingCandidate
{
    /// <summary>
    /// Canonical identifier of the referenced resource (process id, layer id,
    /// service name, template id, etc.).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Kind of resource this candidate represents.
    /// </summary>
    public required CandidateKind Kind { get; init; }

    /// <summary>
    /// Display name for the candidate. Surfaces the catalog title for
    /// datasets and processes.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Deterministic match score in [0,1].
    /// </summary>
    public required double Score { get; init; }

    /// <summary>
    /// Confidence band derived from <see cref="Score"/> using the configured
    /// grounding thresholds.
    /// </summary>
    public required ConfidenceBand ConfidenceBand { get; init; }

    /// <summary>
    /// Evidence tags describing why the candidate ranked. Useful for eval
    /// harnesses and for explaining rankings to operators.
    /// </summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];
}

/// <summary>
/// Candidates grouped by kind. Each list is sorted by descending
/// <see cref="GroundingCandidate.Score"/>.
/// </summary>
public sealed record CandidateRanking
{
    /// <summary>
    /// Dataset candidates (feature layers, raster datasets, service entries).
    /// </summary>
    public IReadOnlyList<GroundingCandidate> Datasets { get; init; } = [];

    /// <summary>
    /// Process candidates from the deterministic process catalog.
    /// </summary>
    public IReadOnlyList<GroundingCandidate> Processes { get; init; } = [];

    /// <summary>
    /// Workflow template candidates. Empty until the template catalog ships
    /// (tracked by honua-server-735 follow-ups).
    /// </summary>
    public IReadOnlyList<GroundingCandidate> Templates { get; init; } = [];

    /// <summary>
    /// Style candidates. Empty until the style catalog ships.
    /// </summary>
    public IReadOnlyList<GroundingCandidate> Styles { get; init; } = [];

    /// <summary>
    /// Deployment target candidates. Empty until the deployment registry ships.
    /// </summary>
    public IReadOnlyList<GroundingCandidate> Deployments { get; init; } = [];
}
