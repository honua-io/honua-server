// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Admin.Routing;

/// <summary>
/// Result of submitting a shadow-topology rebuild (#2718).
/// </summary>
/// <param name="OperationId">Durable execution-job id. Status/cancel/retry ride the shared <c>/api/v1/admin/jobs</c> surface.</param>
/// <param name="Generation">Generation being rebuilt.</param>
/// <param name="Attempt">Newly created rebuild attempt number.</param>
internal sealed record NetworkTopologyRebuildSubmissionResult(string OperationId, long Generation, long Attempt);

/// <summary>
/// Thrown when a rebuild is submitted but the shared durable job infrastructure (Redis-backed
/// <see cref="IExecutionJobStore"/>) is not configured in this deployment profile. Callers map
/// this to HTTP 503 rather than letting a raw DI-resolution failure reach the client.
/// </summary>
internal sealed class NetworkTopologyRebuildUnavailableException(string message) : Exception(message);

/// <summary>
/// Submits an isolated shadow-topology rebuild as a durable
/// <see cref="ExecutionJobKind.NetworkTopologyRebuild"/> execution job (#2718). Creates the
/// rebuild attempt (atomically transitioning the generation <c>dirty</c> -&gt; <c>building</c>)
/// before creating the execution-job record. If that job record is never created, the
/// unclaimed attempt is abandoned (<c>building</c> → <c>failed</c> → <c>dirty</c>) so the
/// generation can be submitted again. A job that was created is left for the worker or the
/// reconciler.
/// </summary>
/// <remarks>
/// Depends on <see cref="IServiceProvider"/> rather than <see cref="IExecutionJobStore"/>
/// directly and resolves it lazily inside <see cref="SubmitAsync"/> (mirrors
/// <c>ConsoleJobService.RequireJobStore</c>). <see cref="IExecutionJobStore"/> is only
/// registered when Redis-backed job orchestration is configured (<c>AddJobOrchestration</c>);
/// a hard constructor dependency would fail ASP.NET Core's <c>ValidateOnBuild</c> service-
/// provider validation on every Redis-less startup, breaking the app for an unrelated reason
/// (secondary-provider-dormant-startup pitfall) instead of staying dormant until invoked.
/// </remarks>
internal sealed partial class NetworkTopologyRebuildSubmissionService
{
    private readonly INetworkTopologyRebuildStore _rebuildStore;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<NetworkTopologyRebuildSubmissionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkTopologyRebuildSubmissionService"/> class.
    /// </summary>
    public NetworkTopologyRebuildSubmissionService(
        INetworkTopologyRebuildStore rebuildStore,
        IServiceProvider serviceProvider,
        ILogger<NetworkTopologyRebuildSubmissionService> logger)
    {
        _rebuildStore = rebuildStore ?? throw new ArgumentNullException(nameof(rebuildStore));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Submits a rebuild for <paramref name="generation"/>, fenced by the caller's expected
    /// row version and source revision. Throws <see cref="NetworkTopologyRebuildConflictException"/>
    /// when the generation is not <c>dirty</c>, the fence is stale, or an attempt is already
    /// active.
    /// </summary>
    public async Task<NetworkTopologyRebuildSubmissionResult> SubmitAsync(
        string datasetId,
        long generation,
        long expectedRowVersion,
        long expectedSourceRevision,
        int srid,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);

        var jobStore = _serviceProvider.GetService<IExecutionJobStore>()
            ?? throw new NetworkTopologyRebuildUnavailableException(
                "Topology rebuild requires durable job orchestration (Redis) to be configured on this deployment.");

        var operationId = $"topology-rebuild-{Guid.NewGuid():N}";
        var attempt = await _rebuildStore.CreateAttemptAsync(
                datasetId, generation, expectedRowVersion, expectedSourceRevision, operationId, cancellationToken)
            .ConfigureAwait(false);

        var spec = NetworkTopologyRebuildExecutionSpecBuilder.Build(
            new NetworkTopologyRebuildJobRequest(datasetId, generation, attempt.Attempt, expectedSourceRevision, srid));

        var now = DateTimeOffset.UtcNow;
        var jobRecord = new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Queued",
            TimeoutPolicy = JobTimeoutPolicy.LongRunning,
            Spec = spec,
        };

        bool created;
        try
        {
            created = await jobStore.TryCreateAsync(jobRecord, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The request was cancelled or the store threw before a durable job existed.
            // Abandon with a token that cannot be cancelled so the generation does not stay building.
            await AbandonUnclaimedAsync(datasetId, generation, attempt.Attempt).ConfigureAwait(false);
            throw;
        }

        if (!created)
        {
            await AbandonUnclaimedAsync(datasetId, generation, attempt.Attempt).ConfigureAwait(false);
            Log.JobRecordCreateFailed(_logger, operationId, datasetId, generation);
            throw new InvalidOperationException($"Failed to create topology-rebuild execution job '{operationId}'.");
        }

        var jobQueue = _serviceProvider.GetService<IJobQueue>();
        if (jobQueue is not null)
        {
            await jobQueue.EnqueueAsync(operationId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        Log.RebuildSubmitted(_logger, operationId, datasetId, generation, attempt.Attempt);
        return new NetworkTopologyRebuildSubmissionResult(operationId, generation, attempt.Attempt);
    }

    private async Task AbandonUnclaimedAsync(string datasetId, long generation, long attempt)
    {
        try
        {
            await _rebuildStore.TryAbandonUnclaimedAttemptAsync(
                    datasetId,
                    generation,
                    attempt,
                    "routing.topology.rebuild_abandoned",
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.UnclaimedAttemptAbandonFailed(_logger, datasetId, generation, attempt, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9270, LogLevel.Information,
            "Submitted topology rebuild job {OperationId} for dataset '{DatasetId}' generation {Generation} attempt {Attempt}")]
        public static partial void RebuildSubmitted(ILogger logger, string operationId, string datasetId, long generation, long attempt);

        [LoggerMessage(9271, LogLevel.Error,
            "Failed to create execution-job record {OperationId} for topology rebuild of dataset '{DatasetId}' generation {Generation}")]
        public static partial void JobRecordCreateFailed(ILogger logger, string operationId, string datasetId, long generation);

        [LoggerMessage(9272, LogLevel.Error,
            "Failed to abandon unclaimed topology rebuild attempt for dataset '{DatasetId}' generation {Generation} attempt {Attempt}")]
        public static partial void UnclaimedAttemptAbandonFailed(ILogger logger, string datasetId, long generation, long attempt, Exception exception);
    }
}
