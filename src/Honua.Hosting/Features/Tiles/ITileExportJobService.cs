// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Infrastructure.Tiles;

/// <summary>
/// The source/resource binding a status, cancel, or result lookup is scoped to. A job
/// submitted by one service family and resource is only reachable through a lookup that
/// carries the matching binding; any other scope resolves to a sanitized not-found so a
/// caller cannot probe for jobs belonging to another service or resource.
/// </summary>
internal readonly record struct TileExportJobScope(TileExportSourceKind SourceKind, string ResourceId);

/// <summary>
/// A freshly minted, time-limited artifact reference returned at result-read time. The
/// download URL is signed on demand and never persisted in job state; the expiry mirrors
/// the stored artifact's retention horizon.
/// </summary>
internal readonly record struct TileExportResultArtifact(
    string DownloadUrl,
    DateTimeOffset? ExpiresAt,
    TileExportPackageFormat Format,
    long? SizeBytes);

/// <summary>
/// Protocol-neutral authority for the durable tile-export job lifecycle. MapServer and
/// ImageServer adapters share this one service so ownership, source binding, idempotent
/// submission, admission, cancellation, and sanitized errors never diverge by protocol.
/// The service creates <see cref="ExecutionJobKind.TileExport"/> records over the canonical
/// execution store/queue and lets the existing reconciler/retry substrate run them; it owns
/// no execution, storage, or retry behavior of its own.
/// </summary>
internal interface ITileExportJobService
{
    /// <summary>
    /// Submits (or idempotently replays) a durable tile-export job. Identical same-principal
    /// replays of a validated idempotency key return the existing job; a payload mismatch or a
    /// cross-principal replay is rejected without exposing another caller's job.
    /// </summary>
    Task<ExecutionJobRecord> SubmitAsync(
        TileExportJobPlan plan,
        string? idempotencyKey,
        string? correlationId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the current job record when the caller owns (or administers) it and the lookup
    /// scope matches the job's source/resource binding; otherwise a sanitized not-found.
    /// </summary>
    Task<ExecutionJobRecord> GetStatusAsync(
        string jobId,
        TileExportJobScope scope,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    /// <summary>
    /// Verifies artifact metadata and expiry for a successfully completed job and mints a fresh
    /// presigned download URL. Fails a precondition when the job is not terminally successful and
    /// a not-found when the artifact is missing or expired.
    /// </summary>
    Task<TileExportResultArtifact> GetResultAsync(
        string jobId,
        TileExportJobScope scope,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    /// <summary>
    /// Cancels a job using the shared compare-and-swap semantics: a claimed/running job has a
    /// durable cancellation signal stamped for the worker to observe, while an unclaimed queued
    /// job transitions straight to cancelled and is removed from the queue. Terminal jobs fail a
    /// precondition; an already-cancelled job is idempotent.
    /// </summary>
    Task CancelAsync(
        string jobId,
        TileExportJobScope scope,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);
}
