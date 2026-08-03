// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>
/// Identifies a single periodic/time-based control-plane maintenance tick. Each kind maps to
/// exactly one background service's idempotent per-tick body (its <c>TickAsync</c>/sweep/flush
/// method). The dispatcher routes a kind to the registered <see cref="IScheduledTickHandler"/>
/// that owns it.
/// <para>
/// This is the bucket-(b) PERIODIC half of the control-plane hybrid-trigger design (option C):
/// on-prem (<c>TriggerMode=Poll</c>) keeps the in-process timers; cloud (<c>TriggerMode=Event</c>)
/// drops the timers and drives each tick from EventBridge Scheduler -> the scheduled-tick endpoint,
/// which calls <see cref="IScheduledTickDispatcher.RunTickAsync"/>.
/// </para>
/// </summary>
public enum ScheduledTickKind
{
    /// <summary>
    /// Cron-triggered workflow scheduler evaluation
    /// (<c>WorkflowSchedulerBackgroundService.TickAsync</c>). Cadence ~1 minute. Idempotent via a
    /// durable per-occurrence claim plus a durable schedule cursor, so double-fire never duplicates
    /// a run.
    /// </summary>
    WorkflowSchedule,

    /// <summary>
    /// Execution-job heartbeat/timeout reaping sweep
    /// (<c>JobReconciliationService.SweepActiveJobsAsync</c>). Cadence ~30s-1min. Idempotent: each
    /// candidate is re-read and mutated only via optimistic CAS, so a duplicate sweep is a no-op.
    /// </summary>
    JobReconciliation,

    /// <summary>
    /// Scheduled tile-cache expiry/invalidation sweep
    /// (<c>TileCacheExpiryHostedService.SweepAsync</c>). Cadence minutes. Re-running re-queues
    /// invalidate jobs whose own pipeline is idempotent.
    /// </summary>
    TileCacheExpiry,

    /// <summary>
    /// Live size-quota / LRU tile-cache eviction sweep
    /// (<c>TileCacheEvictionService.SweepAsync</c>). Cadence minutes. Re-running re-evaluates the
    /// current key index against the quota; a no-op when nothing exceeds it.
    /// </summary>
    TileCacheEviction,

    /// <summary>
    /// Geoprocessing workspace cleanup sweep (<c>WorkspaceCleanupService.RunCleanupAsync</c>).
    /// Cadence minutes-hours. Acts on TTL/expiry state, so re-running finds nothing new.
    /// </summary>
    WorkspaceCleanup,

    /// <summary>
    /// Cloud file-storage expired-file cleanup (<c>FileStorageCleanupService.RunCleanupAsync</c>).
    /// Cadence hours. Deletes already-expired files; re-running is a no-op. On AWS this tick can be
    /// replaced by an S3 lifecycle policy, but the tick is retained for on-prem/portability.
    /// </summary>
    FileStorageCleanup,

    /// <summary>
    /// Referenced raster output orphan reconciliation. Deletes expired attempt staging objects and
    /// unregistered published objects behind the same durable object lease used by publication.
    /// </summary>
    RasterOutputReconciliation,

    /// <summary>
    /// Temporary-file cleanup (<c>TemporaryFileCleanupService.PerformCleanupAsync</c>). Cadence
    /// ~30 minutes. Deletes expired temp files; re-running is a no-op.
    /// </summary>
    TemporaryFileCleanup,

    /// <summary>
    /// Alert digest flush (<c>DigestFlushBackgroundService.FlushAsync</c>). Cadence minutes.
    /// Idempotent via an atomic batch claim; a duplicate flush claims a different (or empty) batch.
    /// </summary>
    DigestFlush
}

/// <summary>
/// A single periodic control-plane tick, exposed so the dispatcher can drive it once on demand
/// (from EventBridge Scheduler under <c>TriggerMode=Event</c>) without hosting an in-process timer.
/// <para>
/// One handler is registered per <see cref="ScheduledTickKind"/>. Implementations live in the
/// assembly that owns the underlying background service (the service types are <c>internal</c>), and
/// wrap that service's already-idempotent per-tick body. Hosting the in-process timer and invoking
/// the tick are independent: in <c>Poll</c> mode the timer runs and the handler is also available;
/// in <c>Event</c> mode the timer is not hosted and the handler is the only driver.
/// </para>
/// </summary>
public interface IScheduledTickHandler
{
    /// <summary>The tick kind this handler owns. Exactly one handler is registered per kind.</summary>
    ScheduledTickKind Kind { get; }

    /// <summary>
    /// Runs the underlying service's per-tick body exactly once. Safe to invoke concurrently with,
    /// or instead of, the in-process timer: the tick bodies are idempotent (claim + cursor, optimistic
    /// CAS, or expiry-state driven), so a single invocation neither duplicates work nor corrupts state.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RunTickAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Single entrypoint that runs one periodic control-plane tick on demand, routing a
/// <see cref="ScheduledTickKind"/> to the <see cref="IScheduledTickHandler"/> that owns it.
/// <para>
/// This mirrors <see cref="IOperationReconcileDispatcher"/> for the PERIODIC (bucket-b) services:
/// the in-process timers (<c>Poll</c>) and EventBridge Scheduler (<c>Event</c>) both ultimately
/// exercise the same idempotent tick body, so a single code path serves on-prem and cloud from one
/// codebase. The dispatcher carries no coordination of its own; the tick bodies' own idempotency
/// makes single, duplicate, and concurrent invocation safe.
/// </para>
/// </summary>
public interface IScheduledTickDispatcher
{
    /// <summary>
    /// Runs the tick for the given kind exactly once and returns. Throws
    /// <see cref="ArgumentOutOfRangeException"/> when no handler is registered for the kind.
    /// </summary>
    /// <param name="kind">The tick to run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RunTickAsync(ScheduledTickKind kind, CancellationToken cancellationToken = default);
}
