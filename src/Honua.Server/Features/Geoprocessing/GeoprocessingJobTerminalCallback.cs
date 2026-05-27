// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.AnalysisContent;
using Honua.Core.Features.AnalysisContent.Abstractions;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// Synchronizes the admin progress store when a geoprocessing execution job
/// reaches a terminal state, keeping the admin operations API consistent with
/// the authoritative <see cref="IExecutionJobStore"/> record.
/// </summary>
internal sealed partial class GeoprocessingJobTerminalCallback(
    IUniversalProgressStore progressStore,
    IProcessCatalog processCatalog,
    IGeoprocessingResultPackageStore? resultPackageStore,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<GeoprocessingJobTerminalCallback> logger) : IJobTerminalCallback
{
    private static readonly TimeSpan ProgressRetention = TimeSpan.FromDays(7);

    public async ValueTask OnTerminalAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
    {
        if (job.Spec.Kind != ExecutionJobKind.Geoprocessing)
        {
            return;
        }

        try
        {
            AnalysisResultPackage? package = null;
            var hasAnalysisContentSource = HasAnalysisContentSource(job);
            if (resultPackageStore != null || hasAnalysisContentSource)
            {
                package = GeoprocessingResultPackageFactory.Create(job, processCatalog);
            }

            if (resultPackageStore != null && package != null)
            {
                await resultPackageStore.SetAsync(
                    job.OperationId,
                    package,
                    ProgressRetention,
                    cancellationToken).ConfigureAwait(false);
            }

            if (hasAnalysisContentSource && package != null)
            {
                await PersistAnalysisContentArtifactsWithScopedStoreAsync(job, package, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
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

            GeoprocessingProgress? updated = job.Status switch
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
                    ErrorMessage = job.ErrorMessage,
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
        catch (Exception ex)
        {
            Log.ProgressSyncFailed(logger, job.OperationId, ex);
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

        [LoggerMessage(8020, LogLevel.Warning, "Failed to synchronize admin progress for terminal job {OperationId}; admin view may be stale until TTL expiry")]
        public static partial void ProgressSyncFailed(ILogger logger, string operationId, Exception exception);
    }
}
