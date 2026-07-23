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
    IScopedJobTokenIssuer? scopedJobTokenIssuer = null) : IJobTerminalCallback
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
    }
}
