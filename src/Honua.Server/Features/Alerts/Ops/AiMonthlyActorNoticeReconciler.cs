// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.ExceptionServices;
using Honua.Core.Features.Security.Admission;
using Honua.Server.Features.Alerts;

namespace Honua.Alerts.Ops;

internal sealed partial class AiMonthlyActorNoticeReconciler(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<AiMonthlyActorNoticeReconciler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        new OutboxReconciliationRunner(timeProvider, Interval).RunAsync(
            ReconcileOnceAsync, exception => LogPassFailed(logger, exception), stoppingToken);

    // Each notice family is published on its own, so a failing monthly publish cannot hold back
    // incident notices (or the reverse). The first failure is rethrown after both have run.
    internal async Task<int> ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        Exception? failure = null;
        var queued = 0;
        if (services.GetService<IAiMonthlyActorNoticeStore>() is not null)
        {
            (queued, failure) = await PublishAsync(
                () => services.GetRequiredService<AiMonthlyActorNoticePublisher>().PublishPendingAsync(cancellationToken),
                queued, failure).ConfigureAwait(false);
        }
        if (services.GetService<IAiIncidentNoticeStore>() is not null)
        {
            (queued, failure) = await PublishAsync(
                () => services.GetRequiredService<AiIncidentNoticePublisher>().PublishPendingAsync(cancellationToken),
                queued, failure).ConfigureAwait(false);
        }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
        return queued;
    }

    private static async Task<(int Queued, Exception? Failure)> PublishAsync(
        Func<Task<int>> publish, int queued, Exception? failure)
    {
        try
        {
            return (queued + await publish().ConfigureAwait(false), failure);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (queued, failure ?? exception);
        }
    }

    [LoggerMessage(9470, LogLevel.Error,
        "Agent spend notice reconciliation failed; pending notices will be retried")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
