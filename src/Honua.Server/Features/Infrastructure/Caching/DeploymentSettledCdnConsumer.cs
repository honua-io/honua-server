// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

#if !HONUA_EXCLUDE_AWS
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Infrastructure.SharedCaching;
using Npgsql;

namespace Honua.Server.Features.Infrastructure.Caching;

// Queue acknowledgment follows every consumer. A failed purge therefore remains retryable.
internal sealed partial class DeploymentSettledCdnConsumer(
    JobStoreDatabase database, IAwsEcsClient ecs, CdnInvalidationService cdn,
    IDeployTargetRegistry targets) : IOpsExternalEventConsumer
{
    public async Task HandleAsync(OpsExternalEvent externalEvent, CancellationToken cancellationToken)
    {
        var deployment = Parse(externalEvent.Body);
        if (deployment is null ||
            !IsConfiguredTarget(deployment, await targets.ListAsync(cancellationToken).ConfigureAwait(false)))
        {
            return;
        }

        await database.ExecuteAsync(async (connection, token) =>
        {
            await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            var scope = new PurgeScope(connection, transaction, deployment);
            await LockServiceAsync(scope, token).ConfigureAwait(false);
            if (await IsRecordedAsync(scope, deployment.DeploymentId, token).ConfigureAwait(false))
            {
                return;
            }

            var primary = await GetSettledPrimaryAsync(scope, token).ConfigureAwait(false);
            if (primary is null)
            {
                return;
            }

            var result = await cdn.InvalidateNowAsync(CdnPaths.Everything, token,
                CdnPurgeCause.Release($"deployment:{externalEvent.Id}")).ConfigureAwait(false);
            if (result.Failed)
            {
                throw new InvalidOperationException("Deployment completion CDN purge failed; retry is required.");
            }

            await RecordAsync(scope, new PurgeRecord(primary.TaskDefinitionArn!, externalEvent.Id), token).ConfigureAwait(false);
            await transaction.CommitSafelyAsync(token).ConfigureAwait(false);
        }, StatementRepeat.SafeToRepeat, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AwsEcsDeploymentState?> GetSettledPrimaryAsync(PurgeScope scope, CancellationToken token)
    {
        var deployment = scope.Deployment;
        var state = await ecs.DescribeServiceAsync(deployment.ClusterArn, deployment.ServiceName,
            deployment.Region, token).ConfigureAwait(false);
        var primary = state.Deployments.SingleOrDefault(item => item.Status == "PRIMARY");
        if (primary is null)
        {
            throw new InvalidOperationException("Deployment completion purge is waiting for the primary deployment.");
        }

        if (primary.DeploymentId != deployment.DeploymentId)
        {
            // DescribeServices is eventually consistent. Retry unknown IDs instead of losing
            // a new completion event; an explicitly superseded deployment needs no purge.
            if (state.Deployments.Any(item => item.DeploymentId == deployment.DeploymentId))
            {
                return null;
            }

            // An unlisted deployment behind a primary whose own completion purge is recorded is a late
            // event, already covered by that purge. Otherwise it may be a new deployment ECS has not shown
            // yet: a new deployment is listed as PRIMARY from creation, minutes before it completes.
            if (primary.DeploymentId is { Length: > 0 } current &&
                await IsRecordedAsync(scope, current, token).ConfigureAwait(false))
            {
                return null;
            }

            throw new InvalidOperationException("Deployment completion is not yet visible in ECS service state.");
        }

        RequireSettled(state, primary);
        return primary;
    }

    private static void RequireSettled(AwsEcsServiceState state, AwsEcsDeploymentState primary)
    {
        if (state.Status != "ACTIVE" || state.DesiredCount <= 0 || state.PendingCount != 0 ||
            state.RunningCount != state.DesiredCount || primary.RolloutState != "COMPLETED" ||
            primary.PendingCount != 0 || primary.DesiredCount != state.DesiredCount ||
            primary.RunningCount != state.DesiredCount || string.IsNullOrWhiteSpace(primary.TaskDefinitionArn) ||
            primary.TaskDefinitionArn != state.TaskDefinitionArn ||
            state.Deployments.Any(item => item != primary && (item.RunningCount != 0 || item.PendingCount != 0)))
        {
            throw new InvalidOperationException("Deployment completion purge is waiting for all old tasks to drain.");
        }
    }

    private static async Task LockServiceAsync(PurgeScope scope, CancellationToken token)
    {
        // Another task handling the same service holds the lock across ECS and CDN calls. Fail fast and let
        // the queue redeliver instead of holding a connection while waiting.
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(hashtextextended(@service, 7392))", scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue("service", scope.Deployment.ServiceArn);
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not true)
        {
            throw new InvalidOperationException("Another task is handling this service's deployment completion; retry is required.");
        }
    }

    private async Task<bool> IsRecordedAsync(PurgeScope scope, string deploymentId, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"SELECT EXISTS(SELECT 1 FROM {database.Table("cdn_deployment_purges")} WHERE cluster_arn = @cluster AND service_arn = @service AND deployment_id = @deployment)", scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue("cluster", scope.Deployment.ClusterArn);
        command.Parameters.AddWithValue("service", scope.Deployment.ServiceArn);
        command.Parameters.AddWithValue("deployment", deploymentId);
        return (bool)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
    }

    private async Task RecordAsync(PurgeScope scope, PurgeRecord record, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"INSERT INTO {database.Table("cdn_deployment_purges")} (cluster_arn, service_arn, deployment_id, task_definition_arn, event_id) VALUES (@cluster, @service, @deployment, @task, @event)", scope.Connection, scope.Transaction);
        AddAddress(command, scope.Deployment);
        command.Parameters.AddWithValue("task", record.TaskDefinition);
        command.Parameters.AddWithValue("event", record.EventId);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static void AddAddress(NpgsqlCommand command, Deployment deployment)
    {
        command.Parameters.AddWithValue("cluster", deployment.ClusterArn);
        command.Parameters.AddWithValue("service", deployment.ServiceArn);
        command.Parameters.AddWithValue("deployment", deployment.DeploymentId);
    }

    private sealed record PurgeScope(NpgsqlConnection Connection, NpgsqlTransaction Transaction, Deployment Deployment);

    private sealed record PurgeRecord(string TaskDefinition, string EventId);
}
#endif
