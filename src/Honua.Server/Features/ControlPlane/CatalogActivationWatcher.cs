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
    private static readonly TimeSpan FirstPurgeBackoff = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan MaximumPurgeBackoff = TimeSpan.FromMinutes(30);

    // Every task runs this watcher. A purge is claimed through a lease that is held for the backoff
    // window and kept after a failure, so a failing CDN sees one attempt per window across the fleet
    // rather than one per task per tick; the local schedule backs off the same way without a lease store.
    private readonly string _leaseOwner = $"catalog-watch:{Environment.MachineName}:{Guid.NewGuid():N}";
    private readonly Dictionary<long, (int Failures, DateTimeOffset NextAttempt)> _purgeBackoff = [];

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

        foreach (var watch in await store.ReadPendingAsync(cancellationToken).ConfigureAwait(false))
        {
            await EvaluateWatchAsync(scope.ServiceProvider, watch, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EvaluateWatchAsync(IServiceProvider scoped, CatalogActivationWatch watch, CancellationToken cancellationToken)
    {
        var store = scoped.GetRequiredService<ICatalogActivationWatchStore>();
        if (watch.RollbackRevision is not null)
        {
            await PurgeRollbackAsync(scoped, store, watch, cancellationToken).ConfigureAwait(false);
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
        if (reason is not null && await store.TryRollbackAsync(new(watch.Id, reason), cancellationToken).ConfigureAwait(false))
        {
            Log.RolledBack(logger, watch.Id, reason);
            var pending = await store.ReadPendingAsync(cancellationToken).ConfigureAwait(false);
            if (pending.FirstOrDefault(candidate => candidate.Id == watch.Id && candidate.RollbackRevision is not null) is { } rollback)
            {
                await PurgeRollbackAsync(scoped, store, rollback, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task PurgeRollbackAsync(IServiceProvider scoped, ICatalogActivationWatchStore store,
        CatalogActivationWatch watch, CancellationToken cancellationToken)
    {
        var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        _purgeBackoff.TryGetValue(watch.Id, out var backoff);
        if (now < backoff.NextAttempt)
        {
            return;
        }

        var window = PurgeBackoff(backoff.Failures);
        var leases = scoped.GetService<ILeaseStore>();
        var claim = new LeaseClaim($"catalog-rollback-purge:{watch.Id}", _leaseOwner);
        if (leases is not null && !await leases.TryAcquireAsync(claim, window, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var result = await invalidation.InvalidateNowAsync(CdnPaths.Everything, cancellationToken,
            CdnPurgeCause.Admin(watch.ChangeId!)).ConfigureAwait(false);
        if (result.Failed)
        {
            // The lease is left to expire so no task retries before the window ends.
            _purgeBackoff[watch.Id] = (backoff.Failures + 1, now + window);
            Log.PurgeDeferred(logger, watch.Id, backoff.Failures + 1, window);
            return;
        }

        await store.CompletePurgeAsync(watch.Id, cancellationToken).ConfigureAwait(false);
        _purgeBackoff.Remove(watch.Id);
        if (leases is not null)
        {
            await leases.ReleaseAsync(claim, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static TimeSpan PurgeBackoff(int failures)
        => failures >= 5 ? MaximumPurgeBackoff : TimeSpan.FromTicks(Math.Min(
            FirstPurgeBackoff.Ticks << failures, MaximumPurgeBackoff.Ticks));

    private static partial class Log
    {
        [LoggerMessage(EventId = 10330, Level = LogLevel.Warning,
            Message = "Catalog activation {ActivationId} was rolled back to a new revision: {Reason}")]
        public static partial void RolledBack(ILogger logger, long activationId, string reason);

        [LoggerMessage(EventId = 10331, Level = LogLevel.Warning,
            Message = "Catalog activation watch could not complete; durable pending work will be retried.")]
        public static partial void WatchUnavailable(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 10332, Level = LogLevel.Warning,
            Message = "CDN purge for rolled-back catalog activation {ActivationId} failed (attempt {Attempts}); retrying after {Backoff}.")]
        public static partial void PurgeDeferred(ILogger logger, long activationId, int attempts, TimeSpan backoff);
    }
}
