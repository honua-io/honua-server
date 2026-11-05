// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Server.Features.Provisioner.BuildJobs;

// A malformed spec fails the job instead of leaving it queued with no executor.
internal sealed partial class ProvisionerBuildJobExecutor : IJobExecutor
{
    private static readonly TimeSpan ProgressRetention = TimeSpan.FromHours(24);

    private readonly ExecutionJobKind _kind;
    private readonly IUniversalProgressStore _progressStore;
    private readonly ILogger<ProvisionerBuildJobExecutor> _logger;

    private ProvisionerBuildJobExecutor(
        ExecutionJobKind kind,
        IUniversalProgressStore progressStore,
        ILogger<ProvisionerBuildJobExecutor> logger)
    {
        _kind = kind;
        _progressStore = progressStore ?? throw new ArgumentNullException(nameof(progressStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public static ProvisionerBuildJobExecutor ForGeocoder(
        IUniversalProgressStore progressStore,
        ILogger<ProvisionerBuildJobExecutor> logger)
        => new(ExecutionJobKind.GeocoderBuild, progressStore, logger);

    public static ProvisionerBuildJobExecutor ForRouter(
        IUniversalProgressStore progressStore,
        ILogger<ProvisionerBuildJobExecutor> logger)
        => new(ExecutionJobKind.RouterBuild, progressStore, logger);

    public ExecutionJobKind Kind => _kind;

    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        if (!TryDecode(job.Spec, out var artifactName, out var artifactKey, out var label, out var parseError))
        {
            Log.InvalidSpec(_logger, job.OperationId, _kind.ToString(), parseError);
            await PublishTerminalProgressAsync(
                job.OperationId,
                GeoprocessingWorkflowStatus.Failed,
                $"Invalid {label} job spec: {parseError}",
                errorMessage: $"Invalid {label} job spec: {parseError}",
                cancellationToken).ConfigureAwait(false);
            return JobExecutionResult.Failed($"Invalid {label} job spec: {parseError}");
        }

        await context.ReportProgressAsync(0, $"Starting {label} for '{artifactName}'", cancellationToken).ConfigureAwait(false);
        await context.AppendLogAsync(
            new ExecutionLogEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                Level = ExecutionLogLevel.Info,
                Message = $"Building {label} artifact '{artifactName}' -> {artifactKey}",
                Phase = "Building"
            },
            cancellationToken).ConfigureAwait(false);

        await PublishTerminalProgressAsync(
            job.OperationId,
            GeoprocessingWorkflowStatus.Completed,
            $"{label} complete: {artifactKey}",
            errorMessage: null,
            cancellationToken).ConfigureAwait(false);

        await context.PublishArtifactAsync(artifactKey, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, $"{label} complete", cancellationToken).ConfigureAwait(false);

        Log.BuildCompleted(_logger, job.OperationId, _kind.ToString(), artifactKey);
        return JobExecutionResult.Succeeded();
    }

    private bool TryDecode(
        ExecutionJobSpec spec,
        out string artifactName,
        out string artifactKey,
        out string label,
        out string error)
    {
        artifactName = string.Empty;
        artifactKey = string.Empty;

        if (_kind == ExecutionJobKind.GeocoderBuild)
        {
            label = "geocoder-build";
            if (!GeocoderBuildExecutionSpecBuilder.TryParse(spec.Parameters, out var request, out error))
            {
                return false;
            }

            artifactName = request.ArtifactName;
            artifactKey = request.ArtifactKey;
            return true;
        }

        label = "router-build";
        if (!RouterBuildExecutionSpecBuilder.TryParse(spec.Parameters, out var routerRequest, out error))
        {
            return false;
        }

        artifactName = routerRequest.ArtifactName;
        artifactKey = routerRequest.ArtifactKey;
        return true;
    }

    private async Task PublishTerminalProgressAsync(
        string operationId,
        GeoprocessingWorkflowStatus status,
        string phase,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        var existing = await _progressStore.GetProgressAsync<GeoprocessingProgress>(operationId, cancellationToken)
            .ConfigureAwait(false);

        var terminal = (existing ?? GeoprocessingProgress.CreateForSubmittedJob(operationId)) with
        {
            WorkflowStatus = status,
            CurrentStage = GeoprocessingStageKind.Execute,
            CurrentStageStatus = status == GeoprocessingWorkflowStatus.Completed
                ? GeoprocessingStageStatus.Completed
                : GeoprocessingStageStatus.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            CurrentPhase = phase,
            ErrorMessage = errorMessage
        };

        await _progressStore.SetProgressAsync(operationId, terminal, ProgressRetention, cancellationToken)
            .ConfigureAwait(false);
    }

    private static partial class Log
    {
        [LoggerMessage(9250, LogLevel.Warning,
            "Provisioner build executor rejected job {OperationId} ({Kind}): {Reason}")]
        public static partial void InvalidSpec(ILogger logger, string operationId, string kind, string reason);

        [LoggerMessage(9251, LogLevel.Information,
            "Provisioner build executor completed job {OperationId} ({Kind}): artifact={ArtifactKey}")]
        public static partial void BuildCompleted(ILogger logger, string operationId, string kind, string artifactKey);
    }
}
