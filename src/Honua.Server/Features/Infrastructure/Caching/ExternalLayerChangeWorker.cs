// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.ControlPlane;
using Honua.Core.Features.Shared.Models;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Infrastructure.Caching;
using Npgsql;

namespace Honua.Server.Features.Infrastructure.Caching;

internal sealed partial class ExternalLayerChangeWorker(
    JobStoreDatabase database,
    OutputCacheInvalidationService invalidation,
    ILogger<ExternalLayerChangeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Bound each drain so a continuous writer cannot prevent cancellation or monopolize the pool.
                for (var count = 0; count < 20 && await ProcessOneAsync(stoppingToken).ConfigureAwait(false); count++)
                {
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogRetry(logger, exception);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        }
    }

    internal Task<bool> ProcessOneAsync(CancellationToken cancellationToken)
        => database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            var change = await ReadNextAsync(connection, transaction, token).ConfigureAwait(false);
            if (change is null)
            {
                return false;
            }

            var (layerId, revision, extent) = change.Value;
            var changeId = string.Create(CultureInfo.InvariantCulture, $"external-layer:{layerId}:{revision}");
            await invalidation.InvalidateExternalEditAsync(layerId, extent, changeId, token).ConfigureAwait(false);
            await using var acknowledge = new NpgsqlCommand(
                $"DELETE FROM {database.Table("external_layer_changes")} WHERE layer_id = @layer AND revision = @revision",
                connection, transaction);
            acknowledge.Parameters.AddWithValue("layer", layerId);
            acknowledge.Parameters.AddWithValue("revision", revision);
            await acknowledge.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await transaction.CommitSafelyAsync(token).ConfigureAwait(false);
            return true;
        }, StatementRepeat.SafeToRepeat, cancellationToken);

    private async Task<(int Layer, long Revision, BoundingBox? Extent)?> ReadNextAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT layer_id, revision, extent FROM {database.Table("external_layer_changes")}
            ORDER BY changed_at, layer_id, revision LIMIT 1 FOR UPDATE SKIP LOCKED
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var extent = reader.IsDBNull(2) ? (BoundingBox?)null : BoundingBox.FromArray(reader.GetFieldValue<double[]>(2), 4326);
        return (reader.GetInt32(0), reader.GetInt64(1), extent);
    }

    [LoggerMessage(EventId = 10420, Level = LogLevel.Warning,
        Message = "External layer invalidation remains queued and will be retried.")]
    private static partial void LogRetry(ILogger logger, Exception exception);
}
