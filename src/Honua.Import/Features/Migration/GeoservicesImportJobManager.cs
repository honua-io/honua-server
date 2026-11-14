// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Migration;

internal sealed class GeoservicesImportJobManager : IDistributedImportJobManager
{
    private readonly ImportExecutionJobQueue<GeoservicesImportRequest, GeoservicesImportProgress> _jobs;

    public GeoservicesImportJobManager(
        IUniversalProgressStore progressStore,
        ILogger<GeoservicesImportJobManager> logger,
        IExecutionJobStore? jobStore = null,
        IJobQueue? jobQueue = null,
        IDistributedLeaderElection? leaderElection = null)
    {
        ArgumentNullException.ThrowIfNull(progressStore);
        LeaderElection = leaderElection ?? new UnelectedLeaderElection();
        _jobs = new ImportExecutionJobQueue<GeoservicesImportRequest, GeoservicesImportProgress>(
            new ImportExecutionJobKindSpec<GeoservicesImportRequest, GeoservicesImportProgress>(
                ExecutionJobKind.GeoservicesImport,
                GeoservicesImportJsonContext.Default.GeoservicesImportRequest,
                GeoservicesImportJsonContext.Default.GeoservicesImportProgress,
                progress => IsTerminalStatus(progress.Status),
                ProjectEndedJob,
                (current, next) => current.Status == GeoservicesImportStatus.Cancelled &&
                    next.DataCommitted && IsTerminalStatus(next.Status)),
            new DistributedProgressStoreAdapter<GeoservicesImportProgress>(progressStore),
            progressStore,
            jobStore,
            jobQueue,
            logger);
    }

    public IDistributedLeaderElection LeaderElection { get; }

    public IDistributedProgressStore<GeoservicesImportProgress> ProgressStore => _jobs.ProgressStore;

    public Task SubmitAsync(
        GeoservicesImportRequest request,
        GeoservicesImportProgress initialProgress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var jobId = request.JobId;
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return _jobs.SubmitAsync(new ImportExecutionSubmission<GeoservicesImportRequest, GeoservicesImportProgress>
        {
            JobId = jobId,
            Request = request,
            InitialProgress = initialProgress,
            WorkloadName = $"geoservices-import:{request.TableName}"
        }, cancellationToken);
    }

    public Task RequestCancellationAsync(string jobId, CancellationToken cancellationToken = default)
        => _jobs.RequestCancellationAsync(jobId, cancellationToken);

    internal GeoservicesImportRequest? ReadRequest(ExecutionJobRecord job) => _jobs.ReadRequest(job);

    internal static bool IsTerminalStatus(GeoservicesImportStatus status)
        => status is GeoservicesImportStatus.Completed
            or GeoservicesImportStatus.Failed
            or GeoservicesImportStatus.Cancelled
            or GeoservicesImportStatus.NeedsReview;

    // The worker writes the terminal progress itself; this covers a worker that stopped first.
    private static GeoservicesImportProgress ProjectEndedJob(GeoservicesImportProgress progress, ExecutionJobRecord job)
        => job.Status == ExecutionJobStatus.Cancelled
            ? progress with
            {
                Status = GeoservicesImportStatus.Cancelled,
                CompletedAt = job.CompletedAt ?? job.UpdatedAt,
                CurrentPhase = "Import cancelled"
            }
            : progress with
            {
                Status = GeoservicesImportStatus.Failed,
                CompletedAt = job.CompletedAt ?? job.UpdatedAt,
                ErrorMessage = job.ErrorMessage ?? "Import failed.",
                CurrentPhase = "Import failed"
            };
}

[JsonSerializable(typeof(GeoservicesImportProgress))]
[JsonSerializable(typeof(GeoservicesImportRequest))]
[JsonSerializable(typeof(GeoservicesCredentialDescriptor))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class GeoservicesImportJsonContext : JsonSerializerContext
{
}
