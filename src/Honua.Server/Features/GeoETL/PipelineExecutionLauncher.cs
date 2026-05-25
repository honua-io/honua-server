// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;

namespace Honua.Server.Features.GeoETL;

/// <summary>
/// Builds and submits an <see cref="ExecutionJobKind.ExtractTransformLoad"/> job for a
/// pipeline through the durable substrate. Creates the durable
/// <see cref="PipelineExecution"/> record and the substrate
/// <see cref="ExecutionJobRecord"/> sharing one operation id, then enqueues the job on
/// <see cref="IJobQueue"/> so the substrate's <c>JobExecutionService</c> claims and runs
/// it through <see cref="PipelineJobExecutor"/>. This is the single submission path used
/// by both the run and dry-run endpoints — the execution-submission gate (ADR-0038)
/// belongs here.
/// </summary>
internal sealed class PipelineExecutionLauncher(
    IExecutionJobStore jobStore,
    IJobQueue jobQueue,
    IPipelineExecutionStore executionStore,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Submits a pipeline run.
    /// </summary>
    /// <param name="definition">Pipeline definition to run.</param>
    /// <param name="dryRun">Whether to run as a dry run (null preview sink).</param>
    /// <param name="requestedBy">Operator identity, when known.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created pipeline execution record.</returns>
    public async Task<PipelineExecution> LaunchAsync(
        PipelineDefinition definition,
        bool dryRun,
        string? requestedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var now = timeProvider.GetUtcNow();
        var executionId = $"geoetl-{Guid.NewGuid():N}";

        var execution = new PipelineExecution
        {
            Id = executionId,
            PipelineId = definition.Id,
            PipelineVersion = definition.Version,
            ExecutionJobId = executionId,
            Status = PipelineExecutionStatus.Pending,
            IsDryRun = dryRun,
            BatchId = executionId,
            CreatedAt = now
        };

        await executionStore.CreateAsync(execution, cancellationToken).ConfigureAwait(false);

        var spec = new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.ExtractTransformLoad,
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = LocalBatchComputeBackend.BackendId,
            WorkloadName = $"geoetl:{definition.Id}",
            // Phase 1 baseline runs only managed connectors in the serving image.
            RuntimeProfile = "managed",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PipelineJobExecutor.PipelineIdKey] = definition.Id,
                [PipelineJobExecutor.ExecutionIdKey] = executionId,
                [PipelineJobExecutor.DryRunKey] = dryRun ? "true" : "false"
            }
        };

        var jobRecord = new ExecutionJobRecord
        {
            OperationId = executionId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Queued",
            Audit = new OperationAuditInfo { RequestedBy = requestedBy },
            Spec = spec
        };

        var created = await jobStore.TryCreateAsync(jobRecord, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!created)
        {
            throw new InvalidOperationException($"Failed to create execution job '{executionId}'.");
        }

        await jobQueue.EnqueueAsync(executionId, cancellationToken: cancellationToken).ConfigureAwait(false);

        return execution;
    }
}
