// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Temporal.Domain;

namespace Honua.Core.Features.Temporal.Abstractions;

/// <summary>
/// Slice-1 temporal history surface for honua-server#1166: capability discovery and as-of reads.
/// </summary>
/// <remarks>
/// <para>
/// This service is intentionally read-only and built on the existing change-tracker primitive
/// (<c>IChangeTracker</c>) plus the storage mapping's temporal column. It does not introduce a parallel
/// history store, and it interoperates with — but does not depend on — named-version editing
/// (honua-server#371): a layer can expose temporal history without participating in version branches.
/// </para>
/// <para>
/// Diff, per-feature timeline, attribution, and governed rollback are deferred to later slices and are
/// not part of this contract. Rollback, when added, must execute through the job runner
/// (Honua.Jobs / Honua.Geoprocessing) rather than a parallel lifecycle.
/// </para>
/// </remarks>
public interface ITemporalHistoryService
{
    /// <summary>
    /// Reports whether the addressed layer supports temporal history reads and which primitives back it.
    /// </summary>
    /// <param name="serviceId">Metadata v2 service id.</param>
    /// <param name="layerId">Service-local layer index.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The capability descriptor for the layer.</returns>
    /// <exception cref="TemporalLayerNotFoundException">Thrown when the service/layer does not resolve.</exception>
    Task<TemporalCapability> GetCapabilityAsync(
        string serviceId,
        int layerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the layer's collapsed feature changes as-of a generation cursor. A timestamp cursor is
    /// rejected in slice 1 (timestamp-to-generation resolution is a deferred #1166 follow-up).
    /// </summary>
    /// <param name="serviceId">Metadata v2 service id.</param>
    /// <param name="layerId">Service-local layer index.</param>
    /// <param name="request">Normalized as-of request (slice 1: generation cursor only).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The deterministic as-of result for the resolved cursor.</returns>
    /// <exception cref="TemporalLayerNotFoundException">Thrown when the service/layer does not resolve.</exception>
    /// <exception cref="TemporalNotSupportedException">Thrown when the layer does not support history reads.</exception>
    /// <exception cref="TemporalValidationException">Thrown when the request is malformed or supplies a timestamp cursor.</exception>
    Task<TemporalAsOfResult> ReadAsOfAsync(
        string serviceId,
        int layerId,
        TemporalAsOfRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes the diff between two checkpoints for a layer (slice 2 of honua-server#1166): summary
    /// counts plus a page of classified feature changes (added/removed/attribute-changed/geometry-changed)
    /// with field-level detail and geometry indicators.
    /// </summary>
    /// <param name="serviceId">Metadata v2 service id.</param>
    /// <param name="layerId">Service-local layer index.</param>
    /// <param name="request">Normalized diff request with base and target checkpoints.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The diff result for the resolved checkpoints.</returns>
    /// <exception cref="TemporalLayerNotFoundException">Thrown when the service/layer does not resolve.</exception>
    /// <exception cref="TemporalNotSupportedException">Thrown when the layer does not support history reads.</exception>
    /// <exception cref="TemporalValidationException">Thrown when the request or a checkpoint is invalid.</exception>
    Task<TemporalDiffResult> DiffAsync(
        string serviceId,
        int layerId,
        TemporalDiffRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a single feature's revision timeline across history (slice 3 of honua-server#1166): ordered
    /// revisions with generation, timestamp, operation, and attribution, subject to the masking policy.
    /// </summary>
    /// <param name="serviceId">Metadata v2 service id.</param>
    /// <param name="layerId">Service-local layer index.</param>
    /// <param name="request">Normalized timeline request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The feature's timeline.</returns>
    /// <exception cref="TemporalLayerNotFoundException">Thrown when the service/layer does not resolve.</exception>
    /// <exception cref="TemporalNotSupportedException">Thrown when the layer does not support history reads.</exception>
    /// <exception cref="TemporalValidationException">Thrown when the request is invalid.</exception>
    Task<TemporalTimeline> GetTimelineAsync(
        string serviceId,
        int layerId,
        TemporalTimelineRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Plans a governed rollback to a target checkpoint (slice 5 of honua-server#1166): reports the
    /// rollback state, affected counts, validation/compatibility findings, and approval requirement.
    /// Planning never mutates data.
    /// </summary>
    /// <param name="serviceId">Metadata v2 service id.</param>
    /// <param name="layerId">Service-local layer index.</param>
    /// <param name="request">Normalized rollback planning request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rollback plan.</returns>
    /// <exception cref="TemporalLayerNotFoundException">Thrown when the service/layer does not resolve.</exception>
    /// <exception cref="TemporalNotSupportedException">Thrown when the layer does not support history reads.</exception>
    /// <exception cref="TemporalValidationException">Thrown when the request or checkpoint is invalid.</exception>
    Task<TemporalRollbackPlan> PlanRollbackAsync(
        string serviceId,
        int layerId,
        TemporalRollbackPlanRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an approved governed rollback by submitting a NEW forward corrective operation through the
    /// job runner (slice 5 of honua-server#1166). Never deletes history; the corrective job creates a new
    /// checkpoint. Returns a job handle for polling.
    /// </summary>
    /// <param name="serviceId">Metadata v2 service id.</param>
    /// <param name="layerId">Service-local layer index.</param>
    /// <param name="request">Normalized rollback execution request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A handle to the submitted corrective job.</returns>
    /// <exception cref="TemporalLayerNotFoundException">Thrown when the service/layer does not resolve.</exception>
    /// <exception cref="TemporalNotSupportedException">Thrown when the layer does not support history reads.</exception>
    /// <exception cref="TemporalValidationException">Thrown when the request or checkpoint is invalid.</exception>
    /// <exception cref="TemporalRollbackNotPermittedException">Thrown when the plan is not runnable or approval is missing.</exception>
    Task<TemporalRollbackJobHandle> ExecuteRollbackAsync(
        string serviceId,
        int layerId,
        TemporalRollbackExecuteRequest request,
        CancellationToken cancellationToken = default);
}
