// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Infrastructure.SharedCaching;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

internal sealed partial class CatalogActivationWatcher(
    IServiceProvider services,
    CdnInvalidationService invalidation,
    ILogger<CatalogActivationWatcher> logger) : BackgroundService
{
    internal static readonly TimeSpan MinimumPurgeBackoff = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan MaximumPurgeBackoff = TimeSpan.FromMinutes(30);

    // Every task runs this watcher. The purge window grows with the rollback's age, which every task
    // computes from the same stored activation time, so restarts and new tasks keep the schedule. A purge
    // is claimed through a lease held for the whole window and never released early, so a failing CDN sees
    // one attempt per window across the fleet; the local schedule applies the same window without a lease store.
    private readonly string _leaseOwner = $"catalog-watch:{Environment.MachineName}:{Guid.NewGuid():N}";
    private readonly Dictionary<long, DateTimeOffset> _nextPurgeAttempt = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Log.WatchUnavailable(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetService<ICatalogActivationWatchStore>();
        if (store is null)
        {
            return;
        }

        var pending = await store.ReadPendingAsync(cancellationToken).ConfigureAwait(false);
        foreach (var id in _nextPurgeAttempt.Keys.Where(id => !pending.Any(watch => watch.Id == id)).ToArray())
        {
            _nextPurgeAttempt.Remove(id);
        }

        foreach (var watch in pending)
        {
            await EvaluateWatchAsync(scope.ServiceProvider, watch, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EvaluateWatchAsync(IServiceProvider scoped, CatalogActivationWatch watch, CancellationToken cancellationToken)
    {
        var store = scoped.GetRequiredService<ICatalogActivationWatchStore>();
        if (watch.RollbackRevision is not null)
        {
            await PurgeRollbackAsync(scoped, watch, cancellationToken).ConfigureAwait(false);
            return;
        }

        var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        if (now > watch.ActivatedAt + CatalogActivationErrorWatch.BakePeriod)
        {
            await store.ExpireAsync(watch.Id, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (scoped.GetService<IOpsHealthRollupStore>() is not { } health)
        {
            return;
        }

        var rows = await health.ReadLatencyAsync(OpsHealthRollupTier.OneMinute,
            watch.ActivatedAt.AddMinutes(-3), now, cancellationToken).ConfigureAwait(false);
        var seconds = scoped.GetService<IOptions<TracingOptions>>()?.Value.ServingLatencyWindowSeconds ?? 300;
        var evidence = new CatalogActivationEvidence(watch.ActivatedAt, TimeSpan.FromSeconds(seconds > 0 ? seconds : 300));
        var reason = CatalogActivationErrorWatch.Evaluate(evidence, now, rows);
        if (reason is null)
        {
            return;
        }

        var outcome = await store.TryRollbackAsync(new(watch.Id, reason), cancellationToken).ConfigureAwait(false);
        if (outcome == CatalogRollbackOutcome.RefusedAccessChange)
        {
            Log.RollbackRefused(logger, watch.Id, reason);
        }

        if (outcome == CatalogRollbackOutcome.RolledBack)
        {
            Log.RolledBack(logger, watch.Id, reason);
            var pending = await store.ReadPendingAsync(cancellationToken).ConfigureAwait(false);
            if (pending.FirstOrDefault(candidate => candidate.Id == watch.Id && candidate.RollbackRevision is not null) is { } rollback)
            {
                await PurgeRollbackAsync(scoped, rollback, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task PurgeRollbackAsync(IServiceProvider scoped, CatalogActivationWatch watch, CancellationToken cancellationToken)
    {
        var store = scoped.GetRequiredService<ICatalogActivationWatchStore>();
        var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        if (_nextPurgeAttempt.TryGetValue(watch.Id, out var next) && now < next)
        {
            return;
        }

        var window = PurgeBackoff(now - watch.ActivatedAt);
        _nextPurgeAttempt[watch.Id] = now + window;
        var leases = scoped.GetService<ILeaseStore>();
        if (leases is not null)
        {
            var claim = new LeaseClaim($"catalog-rollback-purge:{watch.Id}", _leaseOwner);
            if (!await leases.TryAcquireAsync(claim, window, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            // The pending read may predate another task's completed purge; re-read under the lease.
            var current = await store.ReadPendingAsync(cancellationToken).ConfigureAwait(false);
            if (!current.Any(candidate => candidate.Id == watch.Id && candidate.RollbackRevision is not null))
            {
                return;
            }
        }

        var result = await invalidation.InvalidateNowAsync(CdnPaths.Everything, cancellationToken,
            CdnPurgeCause.Admin(watch.ChangeId!)).ConfigureAwait(false);
        if (result.Failed)
        {
            Log.PurgeDeferred(logger, watch.Id, window);
            return;
        }

        await store.CompletePurgeAsync(watch.Id, cancellationToken).ConfigureAwait(false);
        _nextPurgeAttempt.Remove(watch.Id);
    }

    // A quarter of the rollback's age, between one and thirty minutes: frequent while the rollback is
    // fresh, then sparse for a CDN that keeps refusing.
    internal static TimeSpan PurgeBackoff(TimeSpan age)
        => TimeSpan.FromTicks(Math.Clamp(age.Ticks / 4, MinimumPurgeBackoff.Ticks, MaximumPurgeBackoff.Ticks));

    private static partial class Log
    {
        [LoggerMessage(EventId = 10330, Level = LogLevel.Warning,
            Message = "Catalog activation {ActivationId} was rolled back to a new revision: {Reason}")]
        public static partial void RolledBack(ILogger logger, long activationId, string reason);

        [LoggerMessage(EventId = 10331, Level = LogLevel.Warning,
            Message = "Catalog activation watch could not complete; durable pending work will be retried.")]
        public static partial void WatchUnavailable(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 10333, Level = LogLevel.Error,
            Message = "Catalog activation {ActivationId} regressed ({Reason}) but changed access settings, so it was not rolled back automatically; review it.")]
        public static partial void RollbackRefused(ILogger logger, long activationId, string reason);

        [LoggerMessage(EventId = 10332, Level = LogLevel.Warning,
            Message = "CDN purge for rolled-back catalog activation {ActivationId} failed; retrying after {Backoff}.")]
        public static partial void PurgeDeferred(ILogger logger, long activationId, TimeSpan backoff);
    }
}
