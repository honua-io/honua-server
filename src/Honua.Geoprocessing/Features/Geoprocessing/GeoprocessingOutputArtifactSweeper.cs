// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Honua.Geoprocessing;

/// <summary>
/// Dispatches the staged-output orphan sweep as a scheduled control-plane tick so
/// event-triggered (serverless) deployments reclaim staged outputs without hosting
/// the in-process timer (mirrors <see cref="WorkspaceCleanupScheduledTickHandler"/>).
/// </summary>
internal sealed class GeoprocessingOutputArtifactSweeperScheduledTickHandler(
    GeoprocessingOutputArtifactSweeper service)
    : Honua.Core.Features.ControlPlane.Abstractions.IScheduledTickHandler
{
    public Honua.Core.Features.ControlPlane.Abstractions.ScheduledTickKind Kind
        => Honua.Core.Features.ControlPlane.Abstractions.ScheduledTickKind.GeoprocessingOutputSweep;

    public Task RunTickAsync(CancellationToken cancellationToken = default)
        => service.SweepOnceAsync(cancellationToken);
}

/// <summary>
/// Reconciles orphaned staged geoprocessing output objects (#3089): losing-attempt
/// staging left behind by retries, staged-but-never-published objects from crashed
/// attempts, and expired outputs whose job record no longer exists. It never deletes
/// an object that may still be publishing (the current attempt of a live job, or any
/// object younger than the sweep grace), that is being read (an unexpired read
/// lease), or that carries a durable retention hold (a COG-catalog registration
/// outliving the job record). Keys outside the canonical attempt-scoped scheme are
/// never touched.
/// </summary>
internal sealed partial class GeoprocessingOutputArtifactSweeper(
    IGeoprocessingOutputObjectStore store,
    IExecutionJobStore jobStore,
    IOptionsMonitor<GeoprocessingOutputStagingOptions> options,
    ILogger<GeoprocessingOutputArtifactSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.CurrentValue.SweepInterval, stoppingToken).ConfigureAwait(false);
                var result = await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
                if (result.Deleted > 0 || result.Failed > 0)
                {
                    Log.SweepCompleted(logger, result.Scanned, result.Deleted, result.Failed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Deliberately broad: one failed sweep must not stop the background
                // reconciler; the next interval retries.
                Log.SweepFailed(logger, ex);
            }
        }
    }

    /// <summary>Runs one sweep pass. Exposed for deterministic integration testing.</summary>
    internal async Task<SweepResult> SweepOnceAsync(CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        var now = DateTimeOffset.UtcNow;
        var scanned = 0;
        var deleted = 0;
        var failed = 0;

        await foreach (var staged in store.ListAsync(current.KeyPrefix, cancellationToken).ConfigureAwait(false))
        {
            scanned++;
            try
            {
                if (await ShouldDeleteAsync(staged, current, now, cancellationToken).ConfigureAwait(false))
                {
                    // Narrow the check-then-delete window: a reader may have acquired
                    // a lease while ShouldDeleteAsync evaluated the job record. A
                    // residual race remains inherent to the sidecar protocol (POSIX
                    // unlink keeps an already-open read intact); this recheck protects
                    // the acquire-then-open sequence on other filesystems.
                    if (await store.HasActiveReadLeaseAsync(staged.ObjectKey, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    // Registration may establish a durable hold after
                    // ShouldDeleteAsync inspected the object. Recheck it in the same
                    // final guard as the read lease so the catalog can never race a
                    // destructive sweep.
                    if (await store.HasRetentionHoldAsync(staged.ObjectKey, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    await store.DeleteAsync(staged.ObjectKey, cancellationToken).ConfigureAwait(false);
                    deleted++;
                    Log.OrphanDeleted(logger, staged.ObjectKey);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Deliberately broad: skipping one undeletable object must not stop
                // the sweep; it is retried on the next pass.
                failed++;
                Log.OrphanDeleteFailed(logger, staged.ObjectKey, ex);
            }
        }

        return new SweepResult(scanned, deleted, failed);
    }

    private async Task<bool> ShouldDeleteAsync(
        GeoprocessingStagedObjectInfo staged,
        GeoprocessingOutputStagingOptions current,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Never act on young objects: they may be mid-publication (staged bytes whose
        // durable publish has not landed yet).
        var age = now - staged.LastModifiedAt;
        if (age < current.SweepGrace)
        {
            return false;
        }

        // Never act on foreign keys.
        if (!GeoprocessingOutputObjectKeys.TryParse(
                current.KeyPrefix, staged.ObjectKey, out var jobId, out var attemptNumber))
        {
            return false;
        }

        // Never delete an object a caller is actively streaming.
        if (await store.HasActiveReadLeaseAsync(staged.ObjectKey, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // Never delete a registered object. COG-catalog rows are permanent while job
        // records expire from Redis, so the durable retention hold written at
        // registration — not the job record — is what keeps a registered object alive.
        if (await store.HasRetentionHoldAsync(staged.ObjectKey, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var job = await jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            // The job record expired from the store; keep the artifact for the orphan
            // retention window so late readers of long-retained links are not broken.
            return age >= current.OrphanRetention;
        }

        if (job.Status is ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled)
        {
            // A cancelled or failed job never exposes staged output as a successful
            // result; its staging is reclaimable after the grace window.
            return true;
        }

        if (job.Status == ExecutionJobStatus.Succeeded)
        {
            // Keep only the winning published set; unreferenced keys are losing-attempt
            // or superseded staging.
            return !IsReferencedByJob(job, staged.ObjectKey);
        }

        // Queued/Provisioning/Running: reclaim only staging from provably stale
        // attempts. The current attempt (or a future one) is never touched.
        return attemptNumber < job.AttemptCount;
    }

    private static bool IsReferencedByJob(ExecutionJobRecord job, string objectKey)
    {
        var parsedDescriptors = job.ArtifactReferences
            .Select(static reference => RasterOutputJson.TryDeserialize(reference, out var descriptor)
                ? descriptor
                : null)
            .Where(static descriptor => descriptor is StagedObjectRasterOutputDescriptor);
        foreach (var descriptor in parsedDescriptors)
        {
            if (descriptor is StagedObjectRasterOutputDescriptor staged
                && string.Equals(staged.ObjectKey, objectKey, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Outcome of one sweep pass.</summary>
    /// <param name="Scanned">Objects considered.</param>
    /// <param name="Deleted">Objects deleted.</param>
    /// <param name="Failed">Objects whose deletion failed.</param>
    internal readonly record struct SweepResult(int Scanned, int Deleted, int Failed);

    private static partial class Log
    {
        [LoggerMessage(8033, LogLevel.Information,
            "Geoprocessing output sweep finished: scanned {Scanned}, deleted {Deleted}, failed {Failed}")]
        public static partial void SweepCompleted(ILogger logger, int scanned, int deleted, int failed);

        [LoggerMessage(8034, LogLevel.Warning, "Geoprocessing output sweep pass failed; retrying on the next interval")]
        public static partial void SweepFailed(ILogger logger, Exception exception);

        [LoggerMessage(8035, LogLevel.Information, "Deleted orphaned staged output object {ObjectKey}")]
        public static partial void OrphanDeleted(ILogger logger, string objectKey);

        [LoggerMessage(8036, LogLevel.Warning, "Failed to delete orphaned staged output object {ObjectKey}; will retry")]
        public static partial void OrphanDeleteFailed(ILogger logger, string objectKey, Exception exception);
    }
}
