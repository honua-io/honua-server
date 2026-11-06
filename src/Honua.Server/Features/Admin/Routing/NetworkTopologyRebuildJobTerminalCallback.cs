// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Admin.Routing;

internal sealed partial class NetworkTopologyRebuildJobTerminalCallback(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<NetworkTopologyRebuildJobTerminalCallback> logger) : IJobTerminalCallback
{
    private const string AbandonedFailureCode = "routing.topology.rebuild_abandoned";

    public async ValueTask OnTerminalAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
    {
        if (job.Spec.Kind != ExecutionJobKind.NetworkTopologyRebuild
            || job.Status is not (ExecutionJobStatus.Cancelled or ExecutionJobStatus.Failed))
        {
            return;
        }

        if (!NetworkTopologyRebuildExecutionSpecBuilder.TryParse(job.Spec.Parameters, out var request, out _))
        {
            return;
        }

        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetService<INetworkTopologyRebuildStore>();
            if (store is null)
            {
                return;
            }

            var attempt = await store.GetAttemptAsync(
                    request.DatasetId, request.Generation, request.Attempt, CancellationToken.None)
                .ConfigureAwait(false);
            if (attempt is null
                || attempt.State != NetworkTopologyRebuildAttemptState.Building
                || !string.IsNullOrWhiteSpace(attempt.OwnerId))
            {
                return;
            }

            await store.TryAbandonUnclaimedAttemptAsync(
                    request.DatasetId,
                    request.Generation,
                    request.Attempt,
                    AbandonedFailureCode,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.AbandonFailed(logger, job.OperationId, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9285, LogLevel.Error,
            "Topology rebuild terminal callback failed to abandon unclaimed attempt for job {OperationId}")]
        public static partial void AbandonFailed(ILogger logger, string operationId, Exception exception);
    }
}
