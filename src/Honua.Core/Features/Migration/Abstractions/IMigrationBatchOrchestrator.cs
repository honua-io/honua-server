// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Abstractions;

/// <summary>
/// Issue #1253. Footprint-driven batch import orchestrator. Composes the
/// per-layer Geoservices import pipeline (via the shared
/// <see cref="IDistributedImportJobManager"/>) into a single ordered, resumable
/// batch run with aggregated progress, and applies cross-layer relationship
/// classes (issue #1256) once all child layers are published.
/// </summary>
/// <remarks>
/// The orchestrator does not run imports itself: it persists the batch
/// composition through <see cref="IMigrationBatchRunCatalog"/> and queues child
/// layer imports onto the existing distributed job queue. A background service
/// advances the batch by polling child progress and rolling it up. Both
/// <see cref="StartAsync"/> and <see cref="AdvanceAsync"/> are idempotent so a
/// recovering leader can resume a partially-completed batch (re-running failed
/// children, skipping already-succeeded ones).
/// </remarks>
public interface IMigrationBatchOrchestrator
{
    /// <summary>
    /// Start an ordered batch run from a footprint/selection. Computes the child
    /// ordering (relationship origin layers before related layers), persists the
    /// batch and child rows, and queues the first ready children.
    /// </summary>
    /// <param name="request">Footprint selection plus orchestration options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created batch run record.</returns>
    Task<MigrationBatchRunRecord> StartAsync(
        MigrationBatchStartRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Advance a batch: reconcile child statuses from their per-layer import jobs,
    /// queue the next ready children, roll up batch status, and — when all
    /// children are published and relationship-apply was requested — apply the
    /// manifest relationships. Safe to call repeatedly; a no-op once the batch is
    /// terminal.
    /// </summary>
    /// <param name="batchId">Batch identifier to advance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The current batch run record after advancing, or null if unknown.</returns>
    Task<MigrationBatchRunRecord?> AdvanceAsync(
        string batchId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Issue #1253. Footprint selection and options for starting a batch run via
/// <see cref="IMigrationBatchOrchestrator.StartAsync"/>.
/// </summary>
public sealed record MigrationBatchStartRequest
{
    /// <summary>
    /// Source kind identifier such as <c>arcgis-geoservices-rest</c>.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Redacted source URL describing the footprint's origin. Used for operator
    /// display only.
    /// </summary>
    public string SourceUrl { get; init; } = string.Empty;

    /// <summary>
    /// Optional operator-visible source display name.
    /// </summary>
    public string? SourceDisplayName { get; init; }

    /// <summary>
    /// Ordered (or unordered) set of layer-import specifications that make up the
    /// footprint. The orchestrator computes the final execution order from
    /// <see cref="MigrationBatchLayerSpec.DependsOn"/> edges, falling back to the
    /// supplied order for ties.
    /// </summary>
    public required IReadOnlyList<MigrationBatchLayerSpec> Layers { get; init; }

    /// <summary>
    /// Optional manifest JSON body. When present and
    /// <see cref="ApplyRelationships"/> is true, the orchestrator applies the
    /// manifest's relationship classes once all child layers are published.
    /// </summary>
    public string? ManifestBody { get; init; }

    /// <summary>
    /// Whether to apply manifest relationship classes after all child layers are
    /// published (issue #1256). Ignored when <see cref="ManifestBody"/> is null.
    /// </summary>
    public bool ApplyRelationships { get; init; }
}

/// <summary>
/// Issue #1253. One layer-import specification within a batch footprint.
/// </summary>
public sealed record MigrationBatchLayerSpec
{
    /// <summary>
    /// Stable source resource identifier (e.g. <c>resource:Inspections:layer:0</c>).
    /// Must match the manifest's <c>SourceResourceId</c> when relationship-apply is
    /// requested so the published-layer map resolves.
    /// </summary>
    public required string SourceResourceId { get; init; }

    /// <summary>
    /// Source ArcGIS service URL for this layer.
    /// </summary>
    public required string ServiceUrl { get; init; }

    /// <summary>
    /// Source layer id within the service.
    /// </summary>
    public required int LayerId { get; init; }

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
    /// Source resource ids this layer depends on (e.g. relationship origin layers).
    /// The orchestrator imports dependencies first.
    /// </summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];
}
