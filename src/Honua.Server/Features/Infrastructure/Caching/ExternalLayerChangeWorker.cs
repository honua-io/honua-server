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
    private const int LayersPerBatch = 1024;

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
        foreach (var layer in pending)
        {
            acknowledged += await ProcessLayerAsync(layer, cancellationToken).ConfigureAwait(false);
        }

        return acknowledged;
    }

    private async Task<int> ProcessLayerAsync(ExternalLayerChange pending, CancellationToken cancellationToken)
    {
        var now = Clock.GetUtcNow();
        _schedule.TryGetValue(pending.Layer, out var schedule);
        if (now < schedule.NextAttempt)
        {
            return 0;
        }

        var leases = services.GetService<ILeaseStore>();
        var claim = new LeaseClaim($"external-layer-purge:{pending.Layer.ToString(CultureInfo.InvariantCulture)}", _leaseOwner);
        if (leases is not null && !await leases.TryAcquireAsync(claim, PurgeWindow, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        var changeId = string.Create(CultureInfo.InvariantCulture, $"external-layer:{pending.Layer}:{pending.Revision}");
        try
        {
            await invalidation.InvalidateExternalEditAsync(pending.Layer, pending.Extent, changeId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is not OutOfMemoryException)
        {
            var failures = schedule.Failures + 1;
            var backoff = Backoff(failures);
            _schedule[pending.Layer] = new(failures, now + backoff);
            if (leases is not null)
            {
                // Holding the claim for the backoff keeps the other tasks from retrying sooner.
                await leases.RenewAsync(claim, backoff, cancellationToken).ConfigureAwait(false);
            }

            LogLayerRetry(Logger, pending.Layer, failures, exception);
            return 0;
        }

        _schedule[pending.Layer] = new(0, now + PurgeWindow);
        return await AcknowledgeAsync(pending, cancellationToken).ConfigureAwait(false);
    }

    internal static TimeSpan Backoff(int failures)
        => failures >= 5 ? MaximumBackoff : TimeSpan.FromTicks(Math.Min(PurgeWindow.Ticks << failures, MaximumBackoff.Ticks));

    // One row per layer: its latest revision and the extent covering every pending change, which is
    // unbounded when any change has no extent. One purge then covers a layer's whole backlog, and the
    // batch is bounded by the number of tracked layers rather than by the number of changes.
    internal Task<List<ExternalLayerChange>> ReadBatchAsync(CancellationToken cancellationToken)
        => database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"""
                SELECT layer_id, max(revision), bool_or(extent IS NULL),
                       min(extent[1]), min(extent[2]), max(extent[3]), max(extent[4])
                  FROM {database.Table("external_layer_changes")}
                 GROUP BY layer_id
                 ORDER BY min(changed_at), layer_id LIMIT {LayersPerBatch}
                """, connection);
            var changes = new List<ExternalLayerChange>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                changes.Add(new(reader.GetInt32(0), reader.GetInt64(1), reader.GetBoolean(2) ? null : BoundingBox.FromArray(
                    [reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6)], 4326)));
            }

            return changes;
        }, StatementRepeat.SafeToRepeat, cancellationToken);

    // Every revision up to the one read has committed or rolled back: the recording hook holds the
    // layer's revision row from its increment until commit.
    private Task<int> AcknowledgeAsync(ExternalLayerChange purged, CancellationToken cancellationToken)
        => database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand(
                $"DELETE FROM {database.Table("external_layer_changes")} WHERE layer_id = @layer AND revision <= @revision",
                connection);
            command.Parameters.AddWithValue("layer", purged.Layer);
            command.Parameters.AddWithValue("revision", purged.Revision);
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

// A layer's pending changes: the latest revision and the extent covering all of them (null when unbounded).
internal sealed record ExternalLayerChange(int Layer, long Revision, BoundingBox? Extent);
