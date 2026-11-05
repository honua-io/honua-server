// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Admin.Routing;

internal sealed record NetworkTopologyRebuildSubmissionResult(string OperationId, long Generation, long Attempt);

internal sealed class NetworkTopologyRebuildUnavailableException(string message) : Exception(message);

// If the execution-job record is never created the unclaimed attempt is abandoned so the generation can be submitted again, and the job store is resolved lazily so a Redis-less host still starts.
internal sealed partial class NetworkTopologyRebuildSubmissionService
{
    private readonly INetworkTopologyRebuildStore _rebuildStore;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<NetworkTopologyRebuildSubmissionService> _logger;

    public NetworkTopologyRebuildSubmissionService(
        INetworkTopologyRebuildStore rebuildStore,
        IServiceProvider serviceProvider,
        ILogger<NetworkTopologyRebuildSubmissionService> logger)
    {
        _rebuildStore = rebuildStore ?? throw new ArgumentNullException(nameof(rebuildStore));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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
