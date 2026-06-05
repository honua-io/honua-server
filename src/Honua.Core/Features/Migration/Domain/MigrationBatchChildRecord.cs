// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Issue #1253. One ordered child layer import within a
/// <see cref="MigrationBatchRunRecord"/>. Each child maps to a single per-layer
/// Geoservices import job executed by the existing import pipeline; the batch
/// orchestrator sequences children by <see cref="Ordinal"/> while honoring
/// <see cref="DependsOn"/> so relationship origin layers import before the
/// related layers that point at them.
/// </summary>
public sealed record MigrationBatchChildRecord
{
    /// <summary>
    /// Owning batch identifier.
    /// </summary>
    public required string BatchId { get; init; }

    /// <summary>
    /// Zero-based execution ordinal within the batch. Unique per batch and used
    /// as the deterministic sequencing key.
    /// </summary>
    public required int Ordinal { get; init; }

    /// <summary>
    /// Stable source resource identifier from the footprint/manifest (e.g.
    /// <c>resource:Inspections:layer:0</c>). Used to resolve dependency edges and
    /// to build the published-layer map for relationship-apply.
    /// </summary>
    public required string SourceResourceId { get; init; }

    /// <summary>
    /// Source ArcGIS service URL for this layer.
    /// </summary>
    public required string ServiceUrl { get; init; }

    /// <summary>
    /// Source layer id within the service.
    /// </summary>
    public required int SourceLayerId { get; init; }

    /// <summary>
    /// Target PostGIS table name.
    /// </summary>
    public required string TableName { get; init; }

    /// <summary>
    /// Optional target schema for imported operational data.
    /// </summary>
    public string? TargetSchema { get; init; }

    /// <summary>
    /// Optional target Honua service name for auto-publishing.
    /// </summary>
    public string? ServiceName { get; init; }

    /// <summary>
    /// Source resource ids this child depends on. The orchestrator will not start
    /// this child until every dependency has succeeded.
    /// </summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>
    /// Current child status.
    /// </summary>
    public required MigrationBatchChildStatus Status { get; init; }

    /// <summary>
    /// Per-layer import job id once the child has been queued. Null while pending.
    /// </summary>
    public string? JobId { get; init; }

    /// <summary>
    /// Honua layer id assigned at publish time once the child succeeds. Null until
    /// the child publishes a layer.
    /// </summary>
    public int? PublishedLayerId { get; init; }

    /// <summary>
    /// Operator-visible note recorded on failure or review for this child.
    /// </summary>
    public string? StatusNote { get; init; }

    /// <summary>
    /// UTC instant of the last status change.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Lifecycle status for a <see cref="MigrationBatchChildRecord"/>.
/// </summary>
public enum MigrationBatchChildStatus
{
    /// <summary>The child has not been queued yet (waiting on ordering/dependencies).</summary>
    Pending,

    /// <summary>The child has been queued or is executing as a per-layer import job.</summary>
    Running,

    /// <summary>The child import succeeded.</summary>
    Succeeded,

    /// <summary>The child import failed.</summary>
    Failed,

    /// <summary>The child published data but was routed to operator review (issue #1380).</summary>
    NeedsReview,

    /// <summary>The child import was cancelled.</summary>
    Cancelled
}
