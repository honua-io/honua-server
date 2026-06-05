// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Issue #1253. Operator-visible record of a footprint-driven batch migration
/// run. A batch aggregates an ordered set of per-layer import jobs (the
/// "footprint") into a single resumable run with rolled-up progress, and — when
/// requested — applies cross-layer relationship classes (issue #1256) after all
/// child layers are published.
/// </summary>
/// <remarks>
/// <para>
/// The batch run is the parent aggregate over child layer imports executed by
/// the existing per-layer Geoservices import pipeline. Child sequencing,
/// per-child status, and dependency ordering live in
/// <see cref="MigrationBatchChildRecord"/> rows linked by <see cref="BatchId"/>.
/// </para>
/// <para>
/// Privacy posture mirrors <see cref="MigrationRunRecord"/>: <see cref="SourceUrl"/>
/// must already have userinfo, query, and fragment stripped before persistence.
/// </para>
/// </remarks>
public sealed record MigrationBatchRunRecord
{
    /// <summary>
    /// Stable batch identifier (UUID-shaped string). Acts as the primary key.
    /// </summary>
    public required string BatchId { get; init; }

    /// <summary>
    /// Source kind identifier such as <c>arcgis-geoservices-rest</c>.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Redacted source URL (no userinfo, query, or fragment). May be empty when
    /// the batch was driven by an offline footprint only.
    /// </summary>
    public string SourceUrl { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable display name for the source, when known.
    /// </summary>
    public string? SourceDisplayName { get; init; }

    /// <summary>
    /// Current batch status rolled up from child imports.
    /// </summary>
    public required MigrationBatchRunStatus Status { get; init; }

    /// <summary>
    /// UTC instant the batch started.
    /// </summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// UTC instant the batch reached a terminal status. Null while running.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Total number of child layer imports in the batch footprint.
    /// </summary>
    public int TotalChildren { get; init; }

    /// <summary>
    /// Number of child imports that have succeeded.
    /// </summary>
    public int SucceededChildren { get; init; }

    /// <summary>
    /// Number of child imports that have failed.
    /// </summary>
    public int FailedChildren { get; init; }

    /// <summary>
    /// Number of child imports that have been cancelled.
    /// </summary>
    public int CancelledChildren { get; init; }

    /// <summary>
    /// Whether the batch should apply manifest relationship classes (issue #1256)
    /// after all child layers are published.
    /// </summary>
    public bool ApplyRelationships { get; init; }

    /// <summary>
    /// Whether relationship-apply has run for this batch (so the orchestrator does
    /// not re-apply on resume).
    /// </summary>
    public bool RelationshipsApplied { get; init; }

    /// <summary>
    /// Operator-visible note recorded on cancel, failure, or relationship-apply
    /// summary. Free-form, no secrets expected.
    /// </summary>
    public string? StatusNote { get; init; }
}

/// <summary>
/// Lifecycle status for a <see cref="MigrationBatchRunRecord"/>, rolled up from
/// its child imports.
/// </summary>
public enum MigrationBatchRunStatus
{
    /// <summary>One or more children are still queued or running.</summary>
    Running,

    /// <summary>Every child import succeeded.</summary>
    Succeeded,

    /// <summary>At least one child import failed (and the batch stopped advancing).</summary>
    Failed,

    /// <summary>The batch was cancelled by an operator.</summary>
    Cancelled,

    /// <summary>
    /// Every child reached a terminal state but at least one was routed to operator
    /// review (parity gate, issue #1380) without a hard failure.
    /// </summary>
    NeedsReview
}
