// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
            await PurgeRollbackAsync(store, watch, cancellationToken).ConfigureAwait(false);
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
        var evidence = new CatalogActivationEvidence(watch.ActivatedAt, TimeSpan.FromSeconds(Math.Max(1, seconds)));
        var reason = CatalogActivationErrorWatch.Evaluate(evidence, now, rows);
        if (reason is not null && await store.TryRollbackAsync(new(watch.Id, reason), cancellationToken).ConfigureAwait(false))
        {
            Log.RolledBack(logger, watch.Id, reason);
            var pending = await store.ReadPendingAsync(cancellationToken).ConfigureAwait(false);
            if (pending.FirstOrDefault(candidate => candidate.Id == watch.Id && candidate.RollbackRevision is not null) is { } rollback)
            {
                await PurgeRollbackAsync(store, rollback, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task PurgeRollbackAsync(ICatalogActivationWatchStore store, CatalogActivationWatch watch, CancellationToken cancellationToken)
    {
        var result = await invalidation.InvalidateNowAsync(CdnPaths.Everything, cancellationToken,
            CdnPurgeCause.Admin(watch.ChangeId!)).ConfigureAwait(false);
        if (!result.Failed)
        {
            await store.CompletePurgeAsync(watch.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 10330, Level = LogLevel.Warning,
            Message = "Catalog activation {ActivationId} was rolled back to a new revision: {Reason}")]
        public static partial void RolledBack(ILogger logger, long activationId, string reason);

        [LoggerMessage(EventId = 10331, Level = LogLevel.Warning,
            Message = "Catalog activation watch could not complete; durable pending work will be retried.")]
        public static partial void WatchUnavailable(ILogger logger, Exception exception);
    }
}
