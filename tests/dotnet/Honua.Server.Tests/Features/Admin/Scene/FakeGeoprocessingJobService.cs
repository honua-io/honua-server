// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Server.Features.Admin.Scene;

namespace Honua.Server.Tests.Features.Admin.Scene;

/// <summary>
/// In-memory <see cref="IGeoprocessingJobService"/> double for the point-cloud
/// decompression dispatch tests (#1854). Captures the submitted plan and the
/// authorization request, then drives <see cref="GetJobAsync"/> through a scripted
/// status sequence (the last scripted status is sticky; an empty sequence stays
/// <see cref="ExecutionJobStatus.Running"/> forever to exercise the timeout). The
/// result package surfaces a single configurable artifact URI.
/// </summary>
internal sealed class FakeGeoprocessingJobService : IGeoprocessingJobService
{

    public Task<GeoprocessingJobListPage> ListJobsAsync(
        GeoprocessingJobListFilter filter,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new GeoprocessingJobListPage { Items = Array.Empty<ExecutionJobRecord>() });
    private readonly Queue<ExecutionJobStatus> _statuses;
    private readonly string? _artifactUri;
    private string _jobId = string.Empty;
    private ExecutionJobStatus _lastStatus = ExecutionJobStatus.Running;

    public FakeGeoprocessingJobService(Queue<ExecutionJobStatus> statuses, string? artifactUri)
    {
        _statuses = statuses;
        _artifactUri = artifactUri;
    }

    /// <summary>The plan captured at submit time, for assertions.</summary>
    public AnalysisPlan? SubmittedPlan { get; private set; }

    /// <summary>The (resource, operation) the dispatcher authorized against.</summary>
    public (OperatorResourceType Resource, OperatorOperation Operation)? AuthorizedFor { get; private set; }

    /// <summary>Number of <see cref="GetJobAsync"/> polls.</summary>
    public int GetJobCalls { get; private set; }

    /// <summary>When set, <see cref="SubmitJobAsync"/> throws it (simulating a runtime failure).</summary>
    public Exception? SubmitException { get; set; }

    public Task EnsureCallerAuthorizedAsync(
        ClaimsPrincipal principal,
        OperatorResourceType resourceType,
        OperatorOperation operation,
        CancellationToken cancellationToken = default)
    {
        AuthorizedFor = (resourceType, operation);
        return Task.CompletedTask;
    }

    public Task<AnalysisPlan> EnsurePlanExecutionTierAuthorizedAsync(
        AnalysisPlan plan,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
        => Task.FromResult(plan);

    public Task<ExecutionJobRecord> SubmitJobAsync(
        AnalysisPlan plan,
        string? idempotencyKey,
        ClaimsPrincipal principal,
        IReadOnlyDictionary<string, string>? protocolMetadata = null,
        CancellationToken cancellationToken = default)
    {
        if (SubmitException is not null)
        {
            throw SubmitException;
        }

        SubmittedPlan = plan;
        _jobId = $"job-{Guid.NewGuid():N}";
        return Task.FromResult(NewRecord(NextStatus()));
    }

    public Task<ExecutionJobRecord> GetJobAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        GetJobCalls++;
        return Task.FromResult(NewRecord(NextStatus()));
    }

    public Task<AnalysisResultPackage> GetJobResultsAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        var artifacts = _artifactUri is null
            ? Array.Empty<ArtifactRef>()
            : [new ArtifactRef
            {
                ArtifactId = "a1",
                Kind = ArtifactKind.Raster,
                Label = "Decompressed LAS",
                Uri = _artifactUri,
                ContentType = "application/vnd.las",
            }];

        var package = AnalysisResultPackage.CreateCompleted(
            "rp-1",
            new ResultSummary { Title = "Point cloud" },
            artifacts,
            workspaceRefs: [],
            provenance: new ProvenanceRecord
            {
                Sources = [],
                ProcessDefinitions = [GeoprocessingPointCloudDecompressor.ProcessId],
            });
        return Task.FromResult(package);
    }

    public Task CancelJobAsync(string jobId, ClaimsPrincipal principal, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public PlanValidationResult ValidatePlan(AnalysisPlan plan, ClaimsPrincipal principal)
        => throw new NotSupportedException();

    public DryRunResult DryRunPlan(AnalysisPlan plan, ClaimsPrincipal principal)
        => throw new NotSupportedException();

    private ExecutionJobStatus NextStatus()
    {
        if (_statuses.Count > 0)
        {
            _lastStatus = _statuses.Dequeue();
        }
        return _lastStatus;
    }

    private ExecutionJobRecord NewRecord(ExecutionJobStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = _jobId,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "pcloud.translate",
                RuntimeProfile = "native",
            },
        };
    }
}
