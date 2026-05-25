// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using Honua.Core.Features.GeoETL.Services;

namespace Honua.Server.Features.GeoETL;

/// <summary>
/// The GeoETL execution engine, registered as the single
/// <see cref="IJobExecutor"/> for <see cref="ExecutionJobKind.ExtractTransformLoad"/>.
/// The substrate's <c>JobExecutionService</c> discovers it via DI and owns the
/// worker-side polling loop, claim, lease, heartbeat, cancellation propagation, retry,
/// and finalization (ADR-0038 § Substrate consumption). This executor only runs the
/// Source → []Transform → Sink stage chain over <see cref="IPipelineRunObserver"/>,
/// updating the pipeline execution record and emitting the canonical
/// <c>pipeline.*</c> telemetry events through the per-job context.
/// </summary>
/// <remarks>
/// Registered as a singleton (the substrate hosts a singleton
/// <c>JobExecutionService</c> that builds its executor map once). It opens a DI scope
/// per job so scoped stage components — notably the Honua-layer sink's scoped
/// <see cref="IFeatureSinkWriter"/> over the database connection provider — resolve
/// correctly for each run.
/// </remarks>
internal sealed partial class PipelineJobExecutor(
    IServiceScopeFactory scopeFactory,
    ILogger<PipelineJobExecutor> logger) : IJobExecutor
{
    /// <summary>Spec parameter key carrying the pipeline definition id.</summary>
    internal const string PipelineIdKey = "geoetl.pipelineId";

    /// <summary>Spec parameter key carrying the pipeline execution record id.</summary>
    internal const string ExecutionIdKey = "geoetl.executionId";

    /// <summary>Spec parameter key flagging a dry run (value <c>true</c>/<c>false</c>).</summary>
    internal const string DryRunKey = "geoetl.dryRun";

    /// <inheritdoc />
    public ExecutionJobKind Kind => ExecutionJobKind.ExtractTransformLoad;

    /// <inheritdoc />
    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        await using var scope = scopeFactory.CreateAsyncScope();
        var definitionStore = scope.ServiceProvider.GetRequiredService<IPipelineDefinitionStore>();
        var executionStore = scope.ServiceProvider.GetRequiredService<IPipelineExecutionStore>();
        var stageRunner = scope.ServiceProvider.GetRequiredService<PipelineStageRunner>();

        var parameters = job.Spec.Parameters;
        if (!parameters.TryGetValue(PipelineIdKey, out var pipelineId) || string.IsNullOrWhiteSpace(pipelineId))
        {
            return JobExecutionResult.Failed("ExtractTransformLoad job is missing the pipeline id parameter.");
        }

        var executionId = parameters.TryGetValue(ExecutionIdKey, out var execId) && !string.IsNullOrWhiteSpace(execId)
            ? execId
            : job.OperationId;
        var dryRun = parameters.TryGetValue(DryRunKey, out var dryRaw)
            && bool.TryParse(dryRaw, out var parsedDry) && parsedDry;

        var definition = await definitionStore.GetAsync(pipelineId, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            return JobExecutionResult.Failed($"Pipeline '{pipelineId}' was not found.");
        }

        var execution = await executionStore.GetAsync(executionId, cancellationToken).ConfigureAwait(false);
        var batchId = execution?.BatchId ?? executionId;

        Log.PipelineStarted(logger, pipelineId, definition.Version, executionId, dryRun);
        await context.AppendLogAsync(InfoEntry(
            $"pipeline.started pipeline={pipelineId} version={definition.Version} dryRun={dryRun}",
            "Extract"), cancellationToken).ConfigureAwait(false);

        if (execution is not null)
        {
            await executionStore.UpdateAsync(
                execution with { Status = PipelineExecutionStatus.Running },
                cancellationToken).ConfigureAwait(false);
        }

        var observer = new ContextRunObserver(context);

        try
        {
            var result = await stageRunner
                .RunAsync(definition, batchId, dryRun, observer, cancellationToken)
                .ConfigureAwait(false);

            if (execution is not null)
            {
                await executionStore.UpdateAsync(
                    execution with
                    {
                        Status = PipelineExecutionStatus.Succeeded,
                        FeaturesRead = result.FeaturesRead,
                        FeaturesWritten = result.FeaturesWritten,
                        FeaturesQuarantined = result.FeaturesRejected,
                        BatchId = result.BatchId,
                        CompletedAt = DateTimeOffset.UtcNow
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            Log.PipelineCompleted(logger, pipelineId, result.FeaturesRead, result.FeaturesWritten);
            await context.AppendLogAsync(InfoEntry(
                $"pipeline.completed read={result.FeaturesRead} written={result.FeaturesWritten} " +
                $"rejected={result.FeaturesRejected}",
                "Completed"), cancellationToken).ConfigureAwait(false);

            return JobExecutionResult.Succeeded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (execution is not null)
            {
                await executionStore.UpdateAsync(
                    execution with { Status = PipelineExecutionStatus.Cancelled, CompletedAt = DateTimeOffset.UtcNow },
                    CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception ex)
        {
            Log.PipelineFailed(logger, pipelineId, ex);
            await context.AppendLogAsync(
                new ExecutionLogEntry
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Level = ExecutionLogLevel.Error,
                    Message = $"pipeline.failed {ex.Message}",
                    Phase = "Failed"
                },
                CancellationToken.None).ConfigureAwait(false);

            if (execution is not null)
            {
                await executionStore.UpdateAsync(
                    execution with
                    {
                        Status = PipelineExecutionStatus.Failed,
                        ErrorMessage = ex.Message,
                        CompletedAt = DateTimeOffset.UtcNow
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }

            return JobExecutionResult.Failed(ex.Message);
        }
    }

    private static ExecutionLogEntry InfoEntry(string message, string phase)
        => new()
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = ExecutionLogLevel.Info,
            Message = message,
            Phase = phase
        };

    /// <summary>
    /// Bridges <see cref="IPipelineRunObserver"/> to the substrate's per-job
    /// <see cref="IJobExecutionContext"/> so progress and logs flow through the
    /// substrate channels rather than a GeoETL-owned store.
    /// </summary>
    private sealed class ContextRunObserver(IJobExecutionContext context) : IPipelineRunObserver
    {
        public Task ReportProgressAsync(double? percentComplete, string phase, CancellationToken cancellationToken)
            => context.ReportProgressAsync(percentComplete, phase, cancellationToken);

        public Task LogAsync(string message, string phase, CancellationToken cancellationToken)
            => context.AppendLogAsync(
                new ExecutionLogEntry
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Level = ExecutionLogLevel.Info,
                    Message = message,
                    Phase = phase
                },
                cancellationToken);
    }

    private static partial class Log
    {
        [LoggerMessage(9710, LogLevel.Information,
            "pipeline.started pipeline={PipelineId} version={Version} execution={ExecutionId} dryRun={DryRun}")]
        public static partial void PipelineStarted(
            ILogger logger, string pipelineId, int version, string executionId, bool dryRun);

        [LoggerMessage(9711, LogLevel.Information,
            "pipeline.completed pipeline={PipelineId} read={FeaturesRead} written={FeaturesWritten}")]
        public static partial void PipelineCompleted(
            ILogger logger, string pipelineId, long featuresRead, long featuresWritten);

        [LoggerMessage(9712, LogLevel.Error, "pipeline.failed pipeline={PipelineId}")]
        public static partial void PipelineFailed(ILogger logger, string pipelineId, Exception exception);
    }
}
