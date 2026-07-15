// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.Routing.Features.Routing.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Admin.Routing;

/// <summary>
/// Worker-side <see cref="IJobExecutor"/> for <see cref="ExecutionJobKind.NetworkTopologyRebuild"/>
/// jobs (#2718). Runs entirely in-process against Postgres: acquires the rebuild attempt's
/// fencing lease (#2720), materializes an isolated shadow edge/vertex topology from the
/// generation's staged content edits, records per-stage checkpoints so a restarted worker
/// resumes cleanly, computes graph-integrity evidence, and completes or fails the attempt
/// (which drives the owning generation to <c>ready</c>/<c>failed</c>). Never touches the
/// active generation's solve tables.
/// </summary>
/// <remarks>
/// <see cref="IJobExecutor"/> instances are registered and resolved as singletons by
/// <c>JobExecutionService</c>, but the routing stores this executor needs are registered
/// scoped (they are stateless Postgres session wrappers, matching every other
/// network-topology store's lifetime). Rather than change those lifetimes, this executor
/// creates a fresh DI scope per job execution and resolves them from it — the standard
/// pattern for a singleton consuming scoped dependencies.
/// </remarks>
internal sealed partial class NetworkTopologyRebuildJobExecutor : IJobExecutor
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NetworkTopologyRebuildJobExecutor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkTopologyRebuildJobExecutor"/> class.
    /// </summary>
    public NetworkTopologyRebuildJobExecutor(
        IServiceScopeFactory scopeFactory,
        ILogger<NetworkTopologyRebuildJobExecutor> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public ExecutionJobKind Kind => ExecutionJobKind.NetworkTopologyRebuild;

    /// <inheritdoc />
    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        if (!NetworkTopologyRebuildExecutionSpecBuilder.TryParse(job.Spec.Parameters, out var request, out var parseError))
        {
            Log.InvalidSpec(_logger, job.OperationId, parseError);
            return JobExecutionResult.Failed($"Invalid topology-rebuild job spec: {parseError}");
        }

        using var scope = _scopeFactory.CreateScope();
        var rebuildStore = scope.ServiceProvider.GetRequiredService<INetworkTopologyRebuildStore>();
        var shadowBuilder = scope.ServiceProvider.GetRequiredService<NetworkTopologyShadowTopologyBuilder>();

        var ownerId = job.ClaimedBy ?? job.OperationId;
        var leased = await rebuildStore.TryAcquireOrTakeoverLeaseAsync(
                request.DatasetId, request.Generation, request.Attempt, ownerId, LeaseDuration, cancellationToken)
            .ConfigureAwait(false);
        if (leased is null)
        {
            Log.LeaseHeldByAnotherOwner(_logger, job.OperationId, request.DatasetId, request.Generation, request.Attempt);
            return JobExecutionResult.Failed("routing.topology.rebuild_lease_held");
        }

        var token = leased.FencingToken;

        try
        {
            await context.ReportProgressAsync(5, "snapshot", cancellationToken).ConfigureAwait(false);
            await rebuildStore.TryWriteCheckpointAsync(
                    request.DatasetId, request.Generation, request.Attempt, token,
                    NetworkTopologyRebuildStage.Snapshot, NetworkTopologyRebuildCheckpointStatus.Completed,
                    detail: null, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(25, "build", cancellationToken).ConfigureAwait(false);
            var built = await shadowBuilder.BuildAsync(request.DatasetId, request.Generation, request.Attempt, request.Srid, cancellationToken)
                .ConfigureAwait(false);
            await rebuildStore.TryWriteCheckpointAsync(
                    request.DatasetId, request.Generation, request.Attempt, token,
                    NetworkTopologyRebuildStage.Build, NetworkTopologyRebuildCheckpointStatus.Completed,
                    detail: $"edges={built.EdgeCount} vertices={built.VertexCount}", cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(60, "analyze", cancellationToken).ConfigureAwait(false);
            await rebuildStore.TryWriteCheckpointAsync(
                    request.DatasetId, request.Generation, request.Attempt, token,
                    NetworkTopologyRebuildStage.Analyze, NetworkTopologyRebuildCheckpointStatus.Completed,
                    detail: $"self_loops={built.SelfLoopCount}", cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(80, "validate", cancellationToken).ConfigureAwait(false);
            if (built.EdgeCount == 0 || built.VertexCount == 0)
            {
                await rebuildStore.TryWriteCheckpointAsync(
                        request.DatasetId, request.Generation, request.Attempt, token,
                        NetworkTopologyRebuildStage.Validate, NetworkTopologyRebuildCheckpointStatus.Failed,
                        detail: "empty graph", cancellationToken)
                    .ConfigureAwait(false);
                await rebuildStore.TryFailAsync(
                        request.DatasetId, request.Generation, request.Attempt, token,
                        "routing.topology.rebuild_empty_graph", CancellationToken.None)
                    .ConfigureAwait(false);
                return JobExecutionResult.Failed("Rebuild produced an empty shadow topology.");
            }

            await rebuildStore.TryWriteCheckpointAsync(
                    request.DatasetId, request.Generation, request.Attempt, token,
                    NetworkTopologyRebuildStage.Validate, NetworkTopologyRebuildCheckpointStatus.Completed,
                    detail: null, cancellationToken)
                .ConfigureAwait(false);

            var digest = NetworkTopologyShadowTopologyBuilder.ComputeEvidenceDigest(
                request.DatasetId, request.Generation, request.ExpectedSourceRevision, built.EdgeCount, built.VertexCount);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(95, "cleanup", cancellationToken).ConfigureAwait(false);
            await rebuildStore.CleanupOrphanShadowArtifactsAsync(
                    request.DatasetId, request.Generation, keepAttempt: request.Attempt, cancellationToken)
                .ConfigureAwait(false);
            await rebuildStore.TryWriteCheckpointAsync(
                    request.DatasetId, request.Generation, request.Attempt, token,
                    NetworkTopologyRebuildStage.Cleanup, NetworkTopologyRebuildCheckpointStatus.Completed,
                    detail: null, cancellationToken)
                .ConfigureAwait(false);

            var completed = await rebuildStore.TryCompleteAsync(
                    request.DatasetId, request.Generation, request.Attempt, token,
                    built.EdgeTable, built.VertexTable, digest, cancellationToken)
                .ConfigureAwait(false);
            if (!completed)
            {
                Log.FencingLost(_logger, job.OperationId, request.DatasetId, request.Generation, request.Attempt);
                return JobExecutionResult.Failed("routing.topology.rebuild_fencing_lost");
            }

            await context.ReportProgressAsync(100, "ready", cancellationToken).ConfigureAwait(false);
            Log.RebuildCompleted(_logger, job.OperationId, request.DatasetId, request.Generation, built.EdgeCount, built.VertexCount);
            return JobExecutionResult.Succeeded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await rebuildStore.TryFailAsync(
                    request.DatasetId, request.Generation, request.Attempt, token,
                    "routing.topology.rebuild_cancelled", CancellationToken.None)
                .ConfigureAwait(false);
            return JobExecutionResult.Failed("Rebuild cancelled.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.RebuildFailed(_logger, job.OperationId, request.DatasetId, request.Generation, ex);
            await rebuildStore.TryFailAsync(
                    request.DatasetId, request.Generation, request.Attempt, token,
                    "routing.topology.rebuild_failed", CancellationToken.None)
                .ConfigureAwait(false);
            return JobExecutionResult.Failed("Rebuild failed.");
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9260, LogLevel.Warning,
            "Topology rebuild executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidSpec(ILogger logger, string operationId, string reason);

        [LoggerMessage(9261, LogLevel.Information,
            "Topology rebuild job {OperationId} completed for dataset '{DatasetId}' generation {Generation}: edges={EdgeCount} vertices={VertexCount}")]
        public static partial void RebuildCompleted(ILogger logger, string operationId, string datasetId, long generation, long edgeCount, long vertexCount);

        [LoggerMessage(9262, LogLevel.Error,
            "Topology rebuild job {OperationId} failed for dataset '{DatasetId}' generation {Generation}")]
        public static partial void RebuildFailed(ILogger logger, string operationId, string datasetId, long generation, Exception exception);

        [LoggerMessage(9263, LogLevel.Warning,
            "Topology rebuild job {OperationId} could not acquire the rebuild lease for dataset '{DatasetId}' generation {Generation} attempt {Attempt}: held by another owner")]
        public static partial void LeaseHeldByAnotherOwner(ILogger logger, string operationId, string datasetId, long generation, long attempt);

        [LoggerMessage(9264, LogLevel.Warning,
            "Topology rebuild job {OperationId} lost its fencing token before completion for dataset '{DatasetId}' generation {Generation} attempt {Attempt}")]
        public static partial void FencingLost(ILogger logger, string operationId, string datasetId, long generation, long attempt);
    }
}
