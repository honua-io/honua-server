// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

/// <summary>
/// Compiled plan returned by <c>POST /v1/spec/plan</c>. Contains the DAG
/// shape, per-node cost estimate, and structured warnings. Does not represent
/// state or execution progress; use <see cref="SpecApplyEvent"/> for that.
/// </summary>
public sealed record SpecPlan
{
    /// <summary>
    /// Stable plan identifier (GUID). Persisted only in telemetry for S1; not
    /// used to resume apply runs.
    /// </summary>
    public required string PlanId { get; init; }

    /// <summary>
    /// Declared grammar version the spec was authored against.
    /// </summary>
    public required string GrammarVersion { get; init; }

    /// <summary>
    /// Declared process-family version the spec was authored against.
    /// </summary>
    public required string ProcessFamilyVersion { get; init; }

    /// <summary>
    /// DAG nodes in topological order.
    /// </summary>
    public required IReadOnlyList<SpecPlanNode> Nodes { get; init; }

    /// <summary>
    /// Document-level diagnostics (cycles, version skew, aggregate oversize, ...).
    /// </summary>
    public IReadOnlyList<SpecWarning> Warnings { get; init; } = [];
}

/// <summary>
/// A single node in a <see cref="SpecPlan"/>.
/// </summary>
public sealed record SpecPlanNode
{
    /// <summary>
    /// Node identifier as declared in the canonical spec.
    /// </summary>
    public required string NodeId { get; init; }

    /// <summary>
    /// Canonical resource kind.
    /// </summary>
    public required SpecResourceKind Kind { get; init; }

    /// <summary>
    /// Declared operator identifier; null for pure source nodes.
    /// </summary>
    public string? Op { get; init; }

    /// <summary>
    /// Identifiers of nodes this node transitively reads from.
    /// </summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>
    /// Content-hash computed from the spec fragment, input hashes, and
    /// versioning. Used as the cache key for apply output.
    /// </summary>
    public required string ContentHash { get; init; }

    /// <summary>
    /// Cost estimate; components are nullable so "unknown" is distinguishable
    /// from "zero".
    /// </summary>
    public required SpecCostEstimate Cost { get; init; }

    /// <summary>
    /// Per-node diagnostics (missing column, CRS mismatch, ...).
    /// </summary>
    public IReadOnlyList<SpecWarning> Warnings { get; init; } = [];
}

/// <summary>
/// Cost estimate for a single <see cref="SpecPlanNode"/>. Populated by
/// <see cref="Honua.Core.Features.Spec.Abstractions.ISpecCostEstimator"/>
/// using catalog metadata only — never by invoking the process family.
/// </summary>
public sealed record SpecCostEstimate
{
    /// <summary>
    /// Estimated row count, or null when the estimator cannot compute one
    /// from catalog metadata alone.
    /// </summary>
    public long? EstimatedRows { get; init; }

    /// <summary>
    /// Estimated output bytes, or null when unknown.
    /// </summary>
    public long? EstimatedBytes { get; init; }

    /// <summary>
    /// Estimated duration in milliseconds, or null when unknown.
    /// </summary>
    public double? EstimatedDurationMs { get; init; }
}
