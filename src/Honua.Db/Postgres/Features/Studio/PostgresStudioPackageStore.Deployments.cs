// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Studio.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Publishing;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Studio;

internal sealed partial class PostgresStudioPackageStore
{
    private async Task<(StudioPublicationRequest? Existing, bool PayloadMatches)> GetPublicationRequestForReplayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        StudioPublicationRequest requested,
        CancellationToken cancellationToken)
    {
        // Serialize retries of the same immutable request ID across hosts before either
        // can insert a request or deployment. The lock dies with the transaction.
        await using (var gate = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@request_id, 1));",
            connection, transaction))
        {
            gate.Parameters.AddWithValue("@request_id", requested.RequestId.ToString("D"));
            await gate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var sql = $"""
            SELECT request_id, item_id, version_id, intent, status, validation,
                   warning_acknowledgement, requested_by, created_at, deployment_id,
                   governs_studio_routes,
                   (intent IS NOT DISTINCT FROM @requested_intent
                    AND validation = @requested_validation) AS payload_matches
            FROM {_publicationRequestsTable}
            WHERE request_id = @request_id
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@request_id", requested.RequestId);
        var intentJson = requested.Intent is null
            ? null
            : JsonSerializer.Serialize(requested.Intent, StudioJsonContext.Default.StudioPublicationIntent);
        command.Parameters.Add(new NpgsqlParameter("@requested_intent", NpgsqlDbType.Jsonb)
        { Value = (object?)intentJson ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("@requested_validation", NpgsqlDbType.Jsonb)
        { Value = SerializeValidation(requested.Validation) });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (ReadPublicationRequest(reader), reader.GetBoolean(11))
            : (null, false);
    }

    private static bool PublicationRequestMatches(
        StudioPublicationRequest existing,
        StudioPublicationRequest requested,
        bool payloadMatches)
        => existing.ItemId == requested.ItemId
            && existing.VersionId == requested.VersionId
            && existing.Status == requested.Status
            && existing.GovernsStudioRoutes == requested.GovernsStudioRoutes
            && string.Equals(existing.RequestedBy, requested.RequestedBy, StringComparison.Ordinal)
            && string.Equals(existing.WarningAcknowledgement, requested.WarningAcknowledgement, StringComparison.Ordinal)
            && payloadMatches;

    private async Task<StudioContentVersion?> GetVersionForPublicationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid itemId,
        Guid versionId,
        CancellationToken cancellationToken)
    {
        var sql = VersionSelectSql(_versionsTable)
            + " WHERE item_id = @item_id AND version_id = @version_id";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@item_id", itemId);
        command.Parameters.AddWithValue("@version_id", versionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadVersion(reader)
            : null;
    }

    private static Deployment? CreateHostedDeployment(
        StudioContentVersion version,
        StudioContentItemPointers item,
        StudioPublicationRequest request)
    {
        if (request.Status != StudioPublicationRequestStatus.Accepted
            || !request.GovernsStudioRoutes
            || string.IsNullOrWhiteSpace(request.Intent?.Route)
            || version.Envelope.Family is not (StudioPackageFamily.Map or StudioPackageFamily.App))
        {
            return null;
        }

        var source = DeploymentSource.FromStudioPublishedVersion(
            version.ItemId,
            version.VersionId,
            version.ContentHash,
            item.OwnerId,
            request.RequestId,
            version.Envelope.Family);
        return CreateHostedDeploymentCore(item.TenantId, source, request.Intent.Route, request.RequestedBy,
            request.RequestId, "Approved Studio publication");
    }

    private static Deployment CreateHostedDeploymentCore(
        string? tenantId,
        DeploymentSource source,
        string route,
        string? actorId,
        Guid correlationId,
        string reason)
    {
        var target = DeploymentTarget.ForStudioPublishedRoute(route);
        var audit = new OperationAuditInfo
        {
            RequestedBy = actorId,
            TenantId = tenantId,
            Reason = reason,
            CorrelationId = correlationId.ToString("D")
        };

        // The same transaction advances the serving pointer and inserts this deployment.
        // The route is structurally live after commit; an HTTP runtime probe has not yet run.
        return Deployment.CreateDraft(
                $"dep-{Guid.NewGuid():N}", source, target, audit: audit)
            .WithProvisioning("Studio publication accepted", audit)
            .WithRollingOut(reason: "Published pointer staged", audit: audit)
            .WithActive(reason: "Published pointer committed", audit: audit)
            with
        { Runtime = RuntimeState.Unknown() };
    }

    private async Task<Deployment?> ReconcileHostedRollbackAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        StudioContentVersion version,
        StudioContentItemPointers item,
        Guid rollbackRequestId,
        string? actorId,
        CancellationToken cancellationToken)
    {
        var schema = await PostgresPromotionSchema.ResolveAsync(connection, _promotionSchema, cancellationToken, transaction).ConfigureAwait(false);
        var deploymentsTable = SchemaSearchPath.QualifyTable("promotion_deployments", schema);

        // An earlier Studio publication owns the route. Current-only rollbacks do not
        // affect it. Legacy items without a canonical hosted deployment retain their
        // existing pointer behavior without fabricating a route or source receipt.
        var sql = $"""
            SELECT document
            FROM {deploymentsTable}
            WHERE source_kind = @source_kind
              AND status = @active
              AND document -> 'source' ->> 'studioItemId' = @item_id
            FOR UPDATE
            """;
        var active = new List<Deployment>();
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("@source_kind", DeploymentSourceKind.StudioPublishedVersion.ToString());
            command.Parameters.AddWithValue("@active", DeploymentStatus.Active.ToString());
            command.Parameters.AddWithValue("@item_id", version.ItemId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                active.Add(JsonSerializer.Deserialize(reader.GetString(0), DeploymentJsonContext.Default.Deployment)
                    ?? throw new InvalidDataException("A stored deployment document was empty."));
            }
        }

        if (active.Count == 0)
        {
            return null;
        }

        if (active.Count != 1 || active[0].Source.StudioPublicationRequestId is not Guid publicationRequestId)
        {
            throw new InvalidDataException("The active Studio deployment has ambiguous publication provenance.");
        }

        var routeSql = $"SELECT intent ->> 'route' FROM {_publicationRequestsTable} WHERE request_id = @request_id AND item_id = @item_id AND status = @accepted";
        await using var routeCommand = new NpgsqlCommand(routeSql, connection, transaction);
        routeCommand.Parameters.AddWithValue("@request_id", publicationRequestId);
        routeCommand.Parameters.AddWithValue("@item_id", version.ItemId);
        routeCommand.Parameters.AddWithValue("@accepted", ToDbPublicationStatus(StudioPublicationRequestStatus.Accepted));
        var route = await routeCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (string.IsNullOrWhiteSpace(route)
            || !string.Equals(active[0].Target.RoutePrefix, StudioPublishedRoutes.BuildActiveUrl(route), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The active Studio deployment no longer matches its approved route.");
        }

        var source = DeploymentSource.FromStudioPublishedVersion(
            version.ItemId, version.VersionId, version.ContentHash, item.OwnerId,
            publicationRequestId, version.Envelope.Family, rollbackRequestId);
        var successor = CreateHostedDeploymentCore(item.TenantId, source, route, actorId,
            rollbackRequestId, "Studio published-pointer rollback");
        await PersistHostedDeploymentAsync(connection, transaction, successor, cancellationToken).ConfigureAwait(false);
        return successor;
    }

    private async Task PersistHostedDeploymentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Deployment deployment,
        CancellationToken cancellationToken)
    {
        var schema = await PostgresPromotionSchema.ResolveAsync(connection, _promotionSchema, cancellationToken, transaction).ConfigureAwait(false);
        var deploymentsTable = SchemaSearchPath.QualifyTable("promotion_deployments", schema);

        // The Studio pointer CAS has locked this item, and the route advisory lock has
        // serialized competitors for this route. Retire the previous deployment in the same
        // transaction so neither a republish nor a route move leaves two live records.
        var selectSql = $"""
            SELECT document
            FROM {deploymentsTable}
            WHERE status = @active
              AND (target_id = @target_id
                   OR (source_kind = @studio_kind
                       AND document -> 'source' ->> 'studioItemId' = @item_id))
            FOR UPDATE
            """;
        var previous = new List<Deployment>();
        await using (var select = new NpgsqlCommand(selectSql, connection, transaction))
        {
            select.Parameters.AddWithValue("@active", DeploymentStatus.Active.ToString());
            select.Parameters.AddWithValue("@target_id", deployment.Target.TargetId);
            select.Parameters.AddWithValue("@studio_kind", DeploymentSourceKind.StudioPublishedVersion.ToString());
            select.Parameters.AddWithValue("@item_id", deployment.Source.StudioItemId!.Value.ToString("D"));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                previous.Add(JsonSerializer.Deserialize(
                    reader.GetString(0), DeploymentJsonContext.Default.Deployment)
                    ?? throw new InvalidDataException("A stored deployment document was empty."));
            }
        }

        foreach (var current in previous)
        {
            if (current.Source.Kind != DeploymentSourceKind.StudioPublishedVersion
                || current.Source.StudioItemId != deployment.Source.StudioItemId
                || !string.Equals(current.Audit.TenantId, deployment.Audit.TenantId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The Studio route conflicts with an unrelated deployment.");
            }

            var superseded = current.WithSuperseded(deployment.DeploymentId) with
            {
                Version = checked(current.Version + 1)
            };
            var updateSql = $"""
                UPDATE {deploymentsTable}
                SET status = @status, version = @next_version, document = @document,
                    updated_at = @updated_at
                WHERE deployment_id = @deployment_id AND version = @expected_version
                """;
            await using var update = new NpgsqlCommand(updateSql, connection, transaction);
            update.Parameters.AddWithValue("@status", superseded.Status.ToString());
            update.Parameters.AddWithValue("@next_version", superseded.Version);
            update.Parameters.Add(new NpgsqlParameter("@document", NpgsqlDbType.Jsonb)
            {
                Value = JsonSerializer.Serialize(superseded, DeploymentJsonContext.Default.Deployment)
            });
            update.Parameters.AddWithValue("@updated_at", superseded.UpdatedAt);
            update.Parameters.AddWithValue("@deployment_id", superseded.DeploymentId);
            update.Parameters.AddWithValue("@expected_version", current.Version);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("The previous Studio deployment changed concurrently.");
            }
        }

        var insertSql = $"""
            INSERT INTO {deploymentsTable}
                (deployment_id, source_kind, source_id, target_id, status, version,
                 document, created_at, updated_at)
            VALUES
                (@deployment_id, @source_kind, @source_id, @target_id, @status, 0,
                 @document, @created_at, @updated_at)
            """;
        await using var insert = new NpgsqlCommand(insertSql, connection, transaction);
        insert.Parameters.AddWithValue("@deployment_id", deployment.DeploymentId);
        insert.Parameters.AddWithValue("@source_kind", deployment.Source.Kind.ToString());
        insert.Parameters.AddWithValue("@source_id", deployment.Source.SourceId);
        insert.Parameters.AddWithValue("@target_id", deployment.Target.TargetId);
        insert.Parameters.AddWithValue("@status", deployment.Status.ToString());
        insert.Parameters.Add(new NpgsqlParameter("@document", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(deployment, DeploymentJsonContext.Default.Deployment)
        });
        insert.Parameters.AddWithValue("@created_at", deployment.CreatedAt);
        insert.Parameters.AddWithValue("@updated_at", deployment.UpdatedAt);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RetireHostedDeploymentForItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        StudioContentItemPointers item,
        StudioPublicationRequest request,
        CancellationToken cancellationToken)
    {
        var schema = await PostgresPromotionSchema.ResolveAsync(connection, _promotionSchema, cancellationToken, transaction).ConfigureAwait(false);
        var deploymentsTable = SchemaSearchPath.QualifyTable("promotion_deployments", schema);

        // An accepted publication without a hosted route withdraws the previously served
        // version. Keep the deployment projection in the same transaction as the pointer.
        var selectSql = $"""
            SELECT document
            FROM {deploymentsTable}
            WHERE source_kind = @source_kind
              AND status = @active
              AND document -> 'source' ->> 'studioItemId' = @item_id
            FOR UPDATE
            """;
        var active = new List<Deployment>();
        await using (var select = new NpgsqlCommand(selectSql, connection, transaction))
        {
            select.Parameters.AddWithValue("@source_kind", DeploymentSourceKind.StudioPublishedVersion.ToString());
            select.Parameters.AddWithValue("@active", DeploymentStatus.Active.ToString());
            select.Parameters.AddWithValue("@item_id", item.ItemId.ToString("D"));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                active.Add(JsonSerializer.Deserialize(reader.GetString(0), DeploymentJsonContext.Default.Deployment)
                    ?? throw new InvalidDataException("A stored deployment document was empty."));
            }
        }

        if (active.Count > 1)
        {
            throw new InvalidDataException("The Studio item has ambiguous active deployments.");
        }

        foreach (var current in active)
        {
            if (!string.Equals(current.Audit.TenantId, item.TenantId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The active Studio deployment belongs to a different tenant.");
            }

            var audit = new OperationAuditInfo
            {
                RequestedBy = request.RequestedBy,
                TenantId = item.TenantId,
                Reason = "Accepted Studio publication withdrew its hosted route",
                CorrelationId = request.RequestId.ToString("D")
            };
            var retired = current.WithRetired("Studio hosted route withdrawn", audit) with
            {
                Version = checked(current.Version + 1)
            };
            var updateSql = $"""
                UPDATE {deploymentsTable}
                SET status = @status, version = @next_version, document = @document,
                    updated_at = @updated_at
                WHERE deployment_id = @deployment_id AND version = @expected_version
                """;
            await using var update = new NpgsqlCommand(updateSql, connection, transaction);
            update.Parameters.AddWithValue("@status", retired.Status.ToString());
            update.Parameters.AddWithValue("@next_version", retired.Version);
            update.Parameters.Add(new NpgsqlParameter("@document", NpgsqlDbType.Jsonb)
            { Value = JsonSerializer.Serialize(retired, DeploymentJsonContext.Default.Deployment) });
            update.Parameters.AddWithValue("@updated_at", retired.UpdatedAt);
            update.Parameters.AddWithValue("@deployment_id", retired.DeploymentId);
            update.Parameters.AddWithValue("@expected_version", current.Version);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("The previous Studio deployment changed concurrently.");
            }
        }
    }
}
