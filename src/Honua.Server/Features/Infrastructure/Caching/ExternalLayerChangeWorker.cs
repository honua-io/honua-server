// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.ControlPlane;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Core.Features.Shared.Models;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Infrastructure.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Server.Features.Infrastructure.Caching;

// Every task drains the journal. A layer's changes are claimed through a lease that is kept for the
// purge window after success or failure, so each layer sees at most one purge per window across the
// fleet and the changes recorded meanwhile are coalesced into the next one. CDN calls run outside any
// transaction, and a layer whose purge fails backs off without holding up the other layers.
internal sealed partial class ExternalLayerChangeWorker(
    JobStoreDatabase database,
    OutputCacheInvalidationService invalidation,
    IServiceProvider services) : BackgroundService
{
    internal static readonly TimeSpan PurgeWindow = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(15);
    private const int ChangesPerLayer = 200;
    private const int ChangesPerBatch = 1000;

    private readonly string _leaseOwner = $"external-layer:{Environment.MachineName}:{Guid.NewGuid():N}";
    private readonly Dictionary<int, LayerSchedule> _schedule = [];

    private TimeProvider Clock => services.GetService<TimeProvider>() ?? TimeProvider.System;

    private ILogger Logger => services.GetService<ILogger<ExternalLayerChangeWorker>>()
        ?? NullLogger<ExternalLayerChangeWorker>.Instance;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogReadRetry(Logger, exception);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        }
    }

    // Returns the number of journal rows acknowledged.
    internal async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var pending = await ReadBatchAsync(cancellationToken).ConfigureAwait(false);
        foreach (var layer in _schedule.Keys.Where(layer => pending.All(change => change.Layer != layer)).ToArray())
        {
            _schedule.Remove(layer);
        }

        var acknowledged = 0;
        foreach (var layer in pending.GroupBy(change => change.Layer))
        {
            acknowledged += await ProcessLayerAsync(layer.Key, [.. layer], cancellationToken).ConfigureAwait(false);
        }

        return acknowledged;
    }

    private async Task<int> ProcessLayerAsync(int layerId, ExternalLayerChange[] changes, CancellationToken cancellationToken)
    {
        var now = Clock.GetUtcNow();
        _schedule.TryGetValue(layerId, out var schedule);
        if (now < schedule.NextAttempt)
        {
            return 0;
        }

        if (services.GetService<ILeaseStore>() is { } leases && !await leases.TryAcquireAsync(
            new LeaseClaim($"external-layer-purge:{layerId.ToString(CultureInfo.InvariantCulture)}", _leaseOwner),
            PurgeWindow, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        var changeId = string.Create(CultureInfo.InvariantCulture,
            $"external-layer:{layerId}:{changes.Max(change => change.Revision)}");
        try
        {
            await invalidation.InvalidateExternalEditAsync(layerId, CombinedExtent(changes), changeId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            var failures = schedule.Failures + 1;
            _schedule[layerId] = new(failures, now + Backoff(failures));
            LogLayerRetry(Logger, layerId, failures, exception);
            return 0;
        }

        _schedule[layerId] = new(0, now + PurgeWindow);
        return await AcknowledgeAsync(layerId, [.. changes.Select(change => change.Revision)], cancellationToken)
            .ConfigureAwait(false);
    }

    internal static TimeSpan Backoff(int failures)
        => failures >= 5 ? MaximumBackoff : TimeSpan.FromTicks(Math.Min(PurgeWindow.Ticks << failures, MaximumBackoff.Ticks));

    // Any change without an extent, or one crossing the antimeridian, makes the purge layer-wide.
    internal static BoundingBox? CombinedExtent(IReadOnlyList<ExternalLayerChange> changes)
    {
        if (changes.Any(change => change.Extent is not { IsAntimeridianCrossing: false }))
        {
            return null;
        }

        var extents = changes.Select(change => change.Extent!.Value).ToArray();
        return BoundingBox.FromArray(
            [extents.Min(box => box.MinX), extents.Min(box => box.MinY), extents.Max(box => box.MaxX), extents.Max(box => box.MaxY)],
            4326);
    }

    private Task<List<ExternalLayerChange>> ReadBatchAsync(CancellationToken cancellationToken)
        => database.ExecuteAsync(async (connection, _, token) =>
        {
            // The oldest changes of every layer, so one layer's backlog cannot hide the others.
            await using var command = new NpgsqlCommand($"""
                SELECT layer_id, revision, extent FROM (
                    SELECT layer_id, revision, extent, changed_at,
                           row_number() OVER (PARTITION BY layer_id ORDER BY changed_at, revision) AS position
                      FROM {database.Table("external_layer_changes")}) queued
                 WHERE position <= {ChangesPerLayer}
                 ORDER BY changed_at, layer_id, revision LIMIT {ChangesPerBatch}
                """, connection);
            var changes = new List<ExternalLayerChange>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                changes.Add(new(reader.GetInt32(0), reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : BoundingBox.FromArray(reader.GetFieldValue<double[]>(2), 4326)));
            }

            return changes;
        }, StatementRepeat.SafeToRepeat, cancellationToken);

    private Task<int> AcknowledgeAsync(int layerId, long[] revisions, CancellationToken cancellationToken)
        => database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand(
                $"DELETE FROM {database.Table("external_layer_changes")} WHERE layer_id = @layer AND revision = ANY(@revisions)",
                connection);
            command.Parameters.AddWithValue("layer", layerId);
            command.Parameters.AddWithValue("revisions", revisions);
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, StatementRepeat.SafeToRepeat, cancellationToken);

    private readonly record struct LayerSchedule(int Failures, DateTimeOffset NextAttempt);

    [LoggerMessage(EventId = 10420, Level = LogLevel.Warning,
        Message = "External layer changes could not be read; the journal will be read again.")]
    private static partial void LogReadRetry(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 10421, Level = LogLevel.Warning,
        Message = "Invalidation for external changes to layer {LayerId} failed (attempt {Attempts}); the changes stay queued.")]
    private static partial void LogLayerRetry(ILogger logger, int layerId, int attempts, Exception exception);
}

internal sealed record ExternalLayerChange(int Layer, long Revision, BoundingBox? Extent);
