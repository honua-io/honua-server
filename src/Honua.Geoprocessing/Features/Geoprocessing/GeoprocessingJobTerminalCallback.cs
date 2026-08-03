// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.AnalysisContent;
using Honua.Core.Features.AnalysisContent.Abstractions;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Geoprocessing.CustomCode;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Geoprocessing;

/// <summary>
/// Synchronizes the admin progress store when a geoprocessing execution job
/// reaches a terminal state, keeping the admin operations API consistent with
/// the authoritative <see cref="IExecutionJobStore"/> record.
/// </summary>
internal sealed partial class GeoprocessingJobTerminalCallback(
    IUniversalProgressStore progressStore,
    IProcessCatalog processCatalog,
    IOptionsMonitor<GeoprocessingExecutorOptions> executorOptions,
    IGeoprocessingResultPackageStore? resultPackageStore,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<GeoprocessingJobTerminalCallback> logger,
    IScopedJobTokenIssuer? scopedJobTokenIssuer = null,
    IExecutionJobStore? executionJobStore = null) : IJobTerminalCallback
{
    private TimeSpan ProgressRetention => executorOptions.CurrentValue.ResultRetention;

    public async ValueTask OnTerminalAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
    {
        if (job.Spec.Kind != ExecutionJobKind.Geoprocessing)
        {
            return;
        }

        // Revoke the scoped callback token the moment the job reaches a terminal
        // state so the credential cannot outlive the job (Phase-0 invariant #5).
        // The token was injected as env.HONUA_JOB_TOKEN at submit; revoking is
        // idempotent, so a job that minted no token (every non-custom-code job) is a
        // no-op. Done first and best-effort so a revoke failure never blocks the
        // terminal progress/result-package sync below.
        await TryRevokeCustomCodeTokenAsync(job, cancellationToken).ConfigureAwait(false);

        // Tracks whether persisting analysis-content artifacts failed. A successful job
        // must not be reported as Completed when its referenced artifacts are missing from
        // the content store, otherwise GetJobResultsAsync returns dangling references with
        // no retry. When this is set we conservatively fail the terminal-success transition.
        string? artifactPersistenceError = null;

        try
        {
            AnalysisResultPackage? package = null;
            job = await PublishRasterOutputsAsync(job, cancellationToken).ConfigureAwait(false);
            job = await PersistRasterOutputReferencesAsync(job, cancellationToken).ConfigureAwait(false);
            var hasAnalysisContentSource = HasAnalysisContentSource(job);
            if (resultPackageStore != null || hasAnalysisContentSource)
            {
                package = GeoprocessingResultPackageFactory.Create(job, processCatalog);
            }

            // Persist the artifacts to the content store BEFORE writing the result package,
            // so that a Completed result package can never reference artifacts that were
            // never persisted. If artifact persistence fails we skip the result-package
            // write and gate the terminal-success transition below to Failed.
            if (hasAnalysisContentSource && package != null)
            {
                try
                {
                    await PersistAnalysisContentArtifactsWithScopedStoreAsync(job, package, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Intentionally broad: this is the job's terminal callback — it must
                    // never throw, so any persistence failure is captured and gates the
                    // status transition below instead.
                    artifactPersistenceError =
                        "Failed to persist analysis-content artifacts; results would be incomplete.";
                    Log.ArtifactPersistenceFailed(logger, job.OperationId, ex);
                    // Skip the result-package write so we never publish a package whose
                    // artifacts are absent from the content store.
                    package = null;
                }
            }

            if (resultPackageStore != null && package != null)
            {
                await resultPackageStore.SetAsync(
                    job.OperationId,
                    package,
                    ProgressRetention,
                    cancellationToken).ConfigureAwait(false);
            }

            // The manifest is the replay source if CAS persistence, artifact persistence,
            // or result-package storage fails. Delete it only after every required durable
            // projection above succeeds; deletion itself remains best-effort because the
            // bounded orphan reconciler can safely reclaim it later.
            if (artifactPersistenceError is null)
            {
                await DeleteRasterManifestAsync(job, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // PA-209: ensure the terminal-success gate sees the failure so a Succeeded job
            // whose result package was never written does not report Completed to callers.
            artifactPersistenceError = "Failed to persist the result package; results may be unavailable.";
            Log.ResultPackageSyncFailed(logger, job.OperationId, ex);
        }

        try
        {
            var progress = await progressStore.GetProgressAsync<GeoprocessingProgress>(
                job.OperationId, cancellationToken).ConfigureAwait(false);
            if (progress == null)
            {
                return;
            }

            if (progress.Status is OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled)
            {
                return;
            }

            var completedAt = job.CompletedAt ?? DateTimeOffset.UtcNow;

            // A job that otherwise succeeded but whose artifacts failed to persist must not
            // report Completed-with-success; mark it Failed instead (conservative behavior).
            var effectiveStatus = job.Status == ExecutionJobStatus.Succeeded && artifactPersistenceError != null
                ? ExecutionJobStatus.Failed
                : job.Status;

            GeoprocessingProgress? updated = effectiveStatus switch
            {
                ExecutionJobStatus.Succeeded => progress with
                {
                    WorkflowStatus = GeoprocessingWorkflowStatus.Completed,
                    CurrentStageStatus = GeoprocessingStageStatus.Completed,
                    CompletedAt = completedAt,
                    CurrentPhase = "Completed"
                },
                ExecutionJobStatus.Failed => progress with
                {
                    WorkflowStatus = GeoprocessingWorkflowStatus.Failed,
                    CurrentStageStatus = GeoprocessingStageStatus.Failed,
                    CompletedAt = completedAt,
                    ErrorMessage = job.ErrorMessage ?? artifactPersistenceError,
                    CurrentPhase = "Failed"
                },
                ExecutionJobStatus.Cancelled => (GeoprocessingProgress)progress.WithCancellation(
                    completedAt, "Cancelled"),
                _ => null
            };

            if (updated != null)
            {
                await progressStore.SetProgressAsync(
                    job.OperationId, updated, ProgressRetention, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentionally broad: best-effort terminal progress sync — the job itself
            // has already reached a terminal state, so a sync failure here must be
            // logged, not thrown.
            Log.ProgressSyncFailed(logger, job.OperationId, ex);
        }
    }

    private async Task<ExecutionJobRecord> PublishRasterOutputsAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken)
    {
        if (!job.Spec.Parameters.TryGetValue(
                RasterOutputWorkerContract.StoreReferenceParameter,
                out var configuredStoreReference))
        {
            return job;
        }

        if (job.Spec.ContractVersion < RasterOutputContract.JobContractVersion
            || !RasterOutputWorkerContract.IsLogicalStoreReference(configuredStoreReference))
        {
            throw new InvalidDataException("Terminal raster output job has an invalid worker contract.");
        }

        if (executionJobStore is null)
        {
            throw new InvalidOperationException(
                "Durable raster output publication requires an execution job store before any output can become visible.");
        }

        var manifestKey = RasterOutputWorkerContract.BuildManifestObjectKey(
            job.OperationId,
            job.AttemptCount);
        foreach (var reference in job.ArtifactReferences)
        {
            if (RasterOutputArtifactReference.TryParseManifest(
                    reference,
                    out var markerStore,
                    out var markerKey)
                && (!string.Equals(markerStore, configuredStoreReference, StringComparison.Ordinal)
                    || !RasterOutputWorkerContract.TryParseManifestObjectKey(
                        markerKey,
                        out var markerJobId,
                        out var markerAttempt)
                    || !string.Equals(markerJobId, job.OperationId, StringComparison.Ordinal)
                    || markerAttempt > job.AttemptCount))
            {
                throw new InvalidDataException("Raster output manifest marker does not belong to this job's attempts.");
            }
        }

        using var scope = serviceScopeFactory.CreateScope();
        var manifestStore = scope.ServiceProvider.GetRequiredService<IRasterOutputManifestStore>();
        var publisher = scope.ServiceProvider.GetRequiredService<RasterOutputPublisher>();
        var publicationOptions = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<RasterOutputPublicationOptions>>()
            .CurrentValue;
        var manifest = await manifestStore.ReadManifestAsync(
            configuredStoreReference,
            manifestKey,
            cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            if (job.Status == ExecutionJobStatus.Succeeded)
            {
                // A repeated terminal notification can carry the original marker-only
                // snapshot after a prior callback already persisted output descriptors and
                // removed the manifest. Rehydrate that durable projection; otherwise a
                // succeeded v2 job without a manifest remains an integrity failure.
                var persisted = executionJobStore is null
                    ? null
                    : await executionJobStore.GetAsync(job.OperationId, cancellationToken)
                        .ConfigureAwait(false);
                if (persisted is not null
                    && persisted.ArtifactReferences.Any(reference =>
                        RasterOutputArtifactReference.TryParseOutput(reference, out _)))
                {
                    return persisted;
                }

                throw new InvalidDataException("Succeeded raster output job did not publish its attempt manifest.");
            }

            return job with
            {
                ArtifactReferences = job.ArtifactReferences.Where(reference =>
                    !RasterOutputArtifactReference.TryParseManifest(reference, out _, out _)).ToArray()
            };
        }

        if (!string.Equals(manifest.JobId, job.OperationId, StringComparison.Ordinal)
            || manifest.Attempt != job.AttemptCount)
        {
            throw new InvalidDataException("Raster output manifest does not belong to the terminal job attempt.");
        }

        var completionState = job.Status switch
        {
            ExecutionJobStatus.Succeeded => RasterOutputCompletionState.Succeeded,
            ExecutionJobStatus.Failed => RasterOutputCompletionState.Failed,
            ExecutionJobStatus.Cancelled => RasterOutputCompletionState.Cancelled,
            _ => throw new InvalidOperationException("Raster outputs can only be projected for terminal jobs.")
        };
        var publishedAt = job.CompletedAt ?? DateTimeOffset.UtcNow;
        var outputReferences = new List<string>(manifest.Outputs.Count);
        foreach (var stage in manifest.Outputs)
        {
            var result = await publisher.PublishAsync(new RasterOutputPublicationRequest
            {
                Stage = stage,
                CompletionState = completionState,
                RegistrationTarget = new RasterOutputRegistrationTarget(
                    publicationOptions.RegistrationKind,
                    publicationOptions.RegistrationTarget),
                PublishedAt = publishedAt,
                RetainUntil = publishedAt.Add(ProgressRetention)
            }, cancellationToken).ConfigureAwait(false);
            if (result.Output is not null)
            {
                outputReferences.Add(RasterOutputArtifactReference.CreateOutput(result.Output));
            }
        }

        var retainedReferences = job.ArtifactReferences.Where(reference =>
            !RasterOutputArtifactReference.TryParseManifest(reference, out _, out _));
        return job with
        {
            ArtifactReferences = completionState == RasterOutputCompletionState.Succeeded
                ? retainedReferences.Concat(outputReferences).Distinct(StringComparer.Ordinal).ToArray()
                : retainedReferences.ToArray()
        };
    }

    private async Task<ExecutionJobRecord> PersistRasterOutputReferencesAsync(
        ExecutionJobRecord projected,
        CancellationToken cancellationToken)
    {
        if (!projected.Spec.Parameters.ContainsKey(RasterOutputWorkerContract.StoreReferenceParameter))
        {
            return projected;
        }

        if (executionJobStore is null)
        {
            throw new InvalidOperationException(
                "Durable raster output publication requires an execution job store before its manifest can be retired.");
        }

        const int maximumCasAttempts = 3;
        for (var attempt = 0; attempt < maximumCasAttempts; attempt++)
        {
            var current = await executionJobStore.GetAsync(projected.OperationId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Raster output job disappeared before its durable references were persisted.");
            if (current.Status != projected.Status)
            {
                throw new InvalidOperationException(
                    "Raster output job changed terminal status before its references were persisted.");
            }

            var mergedReferences = MergeRasterOutputReferences(
                projected.ArtifactReferences,
                current.ArtifactReferences);
            if (current.ArtifactReferences.SequenceEqual(
                    mergedReferences,
                    StringComparer.Ordinal))
            {
                return current;
            }

            var candidate = current with { ArtifactReferences = mergedReferences };
            if (await executionJobStore.TrySetAsync(
                    candidate,
                    ProgressRetention,
                    cancellationToken).ConfigureAwait(false))
            {
                // Store implementations increment the optimistic version on a successful
                // CAS. Carry that exact durable version into ResultPackageId generation.
                return candidate with { Version = checked(current.Version + 1) };
            }
        }

        throw new InvalidOperationException(
            "Raster output references could not be persisted after repeated version conflicts.");
    }

    private static IReadOnlyList<string> MergeRasterOutputReferences(
        IReadOnlyList<string> projectedReferences,
        IReadOnlyList<string> currentReferences)
    {
        // The callback owns raster manifest/output references only. Preserve any unrelated
        // reference that appeared after its terminal snapshot so a successful CAS retry cannot
        // erase artifacts durably projected by another terminal concern.
        var preservedConcurrentReferences = currentReferences.Where(reference =>
            !RasterOutputArtifactReference.TryParseManifest(reference, out _, out _)
            && !RasterOutputArtifactReference.TryParseOutput(reference, out _));
        return projectedReferences
            .Where(reference => !RasterOutputArtifactReference.TryParseManifest(reference, out _, out _))
            .Concat(preservedConcurrentReferences)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private async Task DeleteRasterManifestAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken)
    {
        if (!job.Spec.Parameters.TryGetValue(
                RasterOutputWorkerContract.StoreReferenceParameter,
                out var storeReference))
        {
            return;
        }

        var manifestKey = RasterOutputWorkerContract.BuildManifestObjectKey(
            job.OperationId,
            job.AttemptCount);
        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var objectStore = scope.ServiceProvider.GetRequiredService<IRasterOutputObjectStore>();
            await objectStore.DeleteAsync(
                storeReference,
                manifestKey,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.ManifestCleanupDeferred(logger, job.OperationId, exception);
        }
    }

    private async Task TryRevokeCustomCodeTokenAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
    {
        if (scopedJobTokenIssuer is null)
        {
            return;
        }

        if (!job.Spec.Parameters.TryGetValue(CustomCodeJobContract.JobTokenEnvParam, out var token) ||
            string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        try
        {
            await scopedJobTokenIssuer.RevokeAsync(token, cancellationToken).ConfigureAwait(false);
            Log.CustomCodeTokenRevoked(logger, job.OperationId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentionally broad: best-effort token revocation on job terminal — a
            // revoke failure must not fail the terminal callback, but it is logged so an
            // un-revoked token is diagnosable.
            Log.CustomCodeTokenRevokeFailed(logger, job.OperationId, ex);
        }
    }

    private static bool HasAnalysisContentSource(ExecutionJobRecord job)
        => job.Spec.Parameters.ContainsKey(AnalysisContentMetadataKeys.ItemId)
           && job.Spec.Parameters.ContainsKey(AnalysisContentMetadataKeys.Version)
           && job.Spec.Parameters.ContainsKey(AnalysisContentMetadataKeys.VersionId);

    private async Task PersistAnalysisContentArtifactsWithScopedStoreAsync(
        ExecutionJobRecord job,
        AnalysisResultPackage package,
        CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var artifactStore = scope.ServiceProvider.GetService<IAnalysisContentStore>();
        if (artifactStore == null)
        {
            return;
        }

        await PersistAnalysisContentArtifactsAsync(
            artifactStore,
            job,
            package,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task PersistAnalysisContentArtifactsAsync(
        IAnalysisContentStore artifactStore,
        ExecutionJobRecord job,
        AnalysisResultPackage package,
        CancellationToken cancellationToken)
    {
        if (!HasAnalysisContentSource(job) ||
            !int.TryParse(
                job.Spec.Parameters[AnalysisContentMetadataKeys.Version],
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var sourceVersion))
        {
            return;
        }

        var sourceItemId = job.Spec.Parameters[AnalysisContentMetadataKeys.ItemId];
        var sourceVersionId = job.Spec.Parameters[AnalysisContentMetadataKeys.VersionId];
        var now = job.CompletedAt ?? DateTimeOffset.UtcNow;

        foreach (var artifact in package.Artifacts)
        {
            var metadata = new Dictionary<string, string>(artifact.Metadata, StringComparer.Ordinal)
            {
                ["resultPackageId"] = package.ResultPackageId
            };
            var provenance = BuildArtifactProvenance(job, package);
            var record = new ResultArtifactRecord
            {
                ArtifactId = artifact.ArtifactId,
                ResultPackageId = package.ResultPackageId,
                JobId = job.OperationId,
                SourceItemId = sourceItemId,
                SourceVersion = sourceVersion,
                SourceVersionId = sourceVersionId,
                Kind = artifact.Kind,
                Label = artifact.Label,
                Uri = artifact.Uri,
                ContentType = artifact.ContentType,
                Metadata = metadata,
                Provenance = provenance,
                RetentionState = ResultArtifactRetentionState.Retained,
                CreatedAt = now
            };

            await artifactStore.UpsertArtifactAsync(record, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Dictionary<string, string> BuildArtifactProvenance(
        ExecutionJobRecord job,
        AnalysisResultPackage package)
    {
        var provenance = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AnalysisContentMetadataKeys.ItemId] = job.Spec.Parameters[AnalysisContentMetadataKeys.ItemId],
            [AnalysisContentMetadataKeys.Version] = job.Spec.Parameters[AnalysisContentMetadataKeys.Version],
            [AnalysisContentMetadataKeys.VersionId] = job.Spec.Parameters[AnalysisContentMetadataKeys.VersionId],
            ["jobId"] = job.OperationId,
            ["resultPackageId"] = package.ResultPackageId,
            ["processDefinitions"] = string.Join(",", package.Provenance.ProcessDefinitions),
            ["generatedArtifactIds"] = string.Join(",", package.Provenance.GeneratedArtifactIds)
        };

        CopyIfPresent(job, provenance, AnalysisContentMetadataKeys.Kind);
        CopyIfPresent(job, provenance, AnalysisContentMetadataKeys.SourceSrid);
        CopyIfPresent(job, provenance, AnalysisContentMetadataKeys.SourceUnits);
        CopyIfPresent(job, provenance, AnalysisContentMetadataKeys.RerunOfJobId);
        CopyIfPresent(job, provenance, AnalysisContentMetadataKeys.RerunOfResultPackageId);

        return provenance;
    }

    private static void CopyIfPresent(
        ExecutionJobRecord job,
        Dictionary<string, string> target,
        string key)
    {
        if (job.Spec.Parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            target[key] = value;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(8019, LogLevel.Warning, "Failed to persist result package for terminal job {OperationId}; job results will be synthesized on demand")]
        public static partial void ResultPackageSyncFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(8021, LogLevel.Error, "Failed to persist analysis-content artifacts for terminal job {OperationId}; the job will be marked Failed to avoid reporting Completed with missing artifacts")]
        public static partial void ArtifactPersistenceFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(8020, LogLevel.Warning, "Failed to synchronize admin progress for terminal job {OperationId}; admin view may be stale until TTL expiry")]
        public static partial void ProgressSyncFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(8022, LogLevel.Information, "Revoked custom-code scoped job token for terminal job {OperationId}")]
        public static partial void CustomCodeTokenRevoked(ILogger logger, string operationId);

        [LoggerMessage(8023, LogLevel.Warning, "Failed to revoke custom-code scoped job token for terminal job {OperationId}; it will expire at its absolute TTL")]
        public static partial void CustomCodeTokenRevokeFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(8024, LogLevel.Warning, "Raster output manifest cleanup was deferred for terminal job {OperationId}; orphan reconciliation will retry")]
        public static partial void ManifestCleanupDeferred(ILogger logger, string operationId, Exception exception);
    }
}
