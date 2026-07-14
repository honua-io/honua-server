// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;

namespace Honua.Server.Features.Admin.Routing;

/// <summary>
/// Result of submitting a shadow-topology rebuild (#2718).
/// </summary>
/// <param name="OperationId">Durable execution-job id. Status/cancel/retry ride the shared <c>/api/v1/admin/jobs</c> surface.</param>
/// <param name="Generation">Generation being rebuilt.</param>
/// <param name="Attempt">Newly created rebuild attempt number.</param>
internal sealed record NetworkTopologyRebuildSubmissionResult(string OperationId, long Generation, long Attempt);

/// <summary>
/// Submits an isolated shadow-topology rebuild as a durable
/// <see cref="ExecutionJobKind.NetworkTopologyRebuild"/> execution job (#2718). Creates the
/// rebuild attempt (atomically transitioning the generation <c>dirty</c> -&gt; <c>building</c>)
/// before creating the execution-job record, so a submission failure after the attempt was
/// created still leaves a durably observable <c>building</c> attempt an operator can inspect
/// or retry rather than a silently lost state transition.
/// </summary>
internal sealed partial class NetworkTopologyRebuildSubmissionService
{
    private readonly INetworkTopologyRebuildStore _rebuildStore;
    private readonly IExecutionJobStore _jobStore;
    private readonly IJobQueue? _jobQueue;
    private readonly ILogger<NetworkTopologyRebuildSubmissionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkTopologyRebuildSubmissionService"/> class.
    /// </summary>
    public NetworkTopologyRebuildSubmissionService(
        INetworkTopologyRebuildStore rebuildStore,
        IExecutionJobStore jobStore,
        ILogger<NetworkTopologyRebuildSubmissionService> logger,
        IJobQueue? jobQueue = null)
    {
        _rebuildStore = rebuildStore ?? throw new ArgumentNullException(nameof(rebuildStore));
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _jobQueue = jobQueue;
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

        var created = await _jobStore.TryCreateAsync(jobRecord, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!created)
        {
            // Extremely unlikely (GUID collision); the attempt is left in `building` for an
            // operator to inspect/retry rather than silently discarded.
            Log.JobRecordCreateFailed(_logger, operationId, datasetId, generation);
            throw new InvalidOperationException($"Failed to create topology-rebuild execution job '{operationId}'.");
        }

        if (_jobQueue is not null)
        {
            await _jobQueue.EnqueueAsync(operationId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        Log.RebuildSubmitted(_logger, operationId, datasetId, generation, attempt.Attempt);
        return new NetworkTopologyRebuildSubmissionResult(operationId, generation, attempt.Attempt);
    }

    private static partial class Log
    {
        [LoggerMessage(9270, LogLevel.Information,
            "Submitted topology rebuild job {OperationId} for dataset '{DatasetId}' generation {Generation} attempt {Attempt}")]
        public static partial void RebuildSubmitted(ILogger logger, string operationId, string datasetId, long generation, long attempt);

        [LoggerMessage(9271, LogLevel.Error,
            "Failed to create execution-job record {OperationId} for topology rebuild of dataset '{DatasetId}' generation {Generation}")]
        public static partial void JobRecordCreateFailed(ILogger logger, string operationId, string datasetId, long generation);
    }
}
