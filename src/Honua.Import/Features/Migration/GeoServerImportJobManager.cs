// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Migration;

internal sealed class GeoServerImportJobManager
{
    private readonly IUniversalProgressStore _universalProgressStore;
    private readonly ImportExecutionJobQueue<GeoServerImportRequest, GeoServerImportProgress> _jobs;

    public GeoServerImportJobManager(
        IUniversalProgressStore universalProgressStore,
        ILogger<GeoServerImportJobManager> logger,
        IExecutionJobStore? jobStore = null,
        IJobQueue? jobQueue = null)
    {
        _universalProgressStore = universalProgressStore ?? throw new ArgumentNullException(nameof(universalProgressStore));
        _jobs = new ImportExecutionJobQueue<GeoServerImportRequest, GeoServerImportProgress>(
            new ImportExecutionJobKindSpec<GeoServerImportRequest, GeoServerImportProgress>(
                ExecutionJobKind.GeoServerImport,
                GeoServerImportApiJsonContext.Default.GeoServerImportRequest,
                GeoServerImportApiJsonContext.Default.GeoServerImportProgress,
                progress => IsTerminalStatus(progress.Status),
                ProjectEndedJob),
            new GeoServerImportProgressStore(universalProgressStore),
            universalProgressStore,
            jobStore,
            jobQueue,
            logger);
    }

    internal IDistributedProgressStore<GeoServerImportProgress> ProgressStore => _jobs.ProgressStore;

    internal Task SubmitAsync(
        GeoServerImportRequest request,
        GeoServerImportProgress initialProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var jobId = request.JobId;
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return _jobs.SubmitAsync(new ImportExecutionSubmission<GeoServerImportRequest, GeoServerImportProgress>
        {
            JobId = jobId,
            Request = request,
            InitialProgress = initialProgress,
            WorkloadName = "geoserver-import"
        }, cancellationToken);
    }

    internal Task RequestCancellationAsync(string jobId, CancellationToken cancellationToken)
        => _jobs.RequestCancellationAsync(jobId, cancellationToken);

    internal IDistributedProgressStore<GeoServerImportProgress> ProgressStoreForAttempt(ExecutionJobRecord job)
        => _jobs.ProgressStoreForAttempt(job);

    internal GeoServerImportRequest? ReadRequest(ExecutionJobRecord job) => _jobs.ReadRequest(job);

    internal Task<ProgressCompareAndSetResult> TryTransitionAsync(
        string jobId, GeoServerImportProgress progress, OperationStatus expectedStatus,
        CancellationToken cancellationToken)
        => _universalProgressStore.TrySetProgressAsync(
            new OperationProgressWrite(jobId, progress, ImportExecutionJobQueue<GeoServerImportRequest, GeoServerImportProgress>.Retention),
            expectedStatus,
            cancellationToken);

    internal static bool IsTerminalStatus(GeoServerImportStatus status)
        => status is GeoServerImportStatus.Completed or GeoServerImportStatus.Failed or GeoServerImportStatus.Cancelled;

    // The worker writes the terminal progress itself; this covers a worker that stopped first.
    private static GeoServerImportProgress ProjectEndedJob(GeoServerImportProgress progress, ExecutionJobRecord job)
        => job.Status == ExecutionJobStatus.Cancelled
            ? progress with
            {
                Status = GeoServerImportStatus.Cancelled,
                CompletedAt = job.CompletedAt ?? job.UpdatedAt,
                CurrentPhase = "Import cancelled"
            }
            : progress with
            {
                Status = GeoServerImportStatus.Failed,
                CompletedAt = job.CompletedAt ?? job.UpdatedAt,
                ErrorMessage = job.ErrorMessage ?? "Import failed.",
                CurrentPhase = "Import failed"
            };
}
