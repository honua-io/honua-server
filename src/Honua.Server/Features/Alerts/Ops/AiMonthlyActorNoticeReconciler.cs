// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

    internal async Task<int> ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var queued = 0;
        if (scope.ServiceProvider.GetService<IAiMonthlyActorNoticeStore>() is not null)
        {
            queued += await scope.ServiceProvider.GetRequiredService<AiMonthlyActorNoticePublisher>()
                .PublishPendingAsync(cancellationToken).ConfigureAwait(false);
        }
        if (scope.ServiceProvider.GetService<IAiIncidentNoticeStore>() is not null)
        {
            queued += await scope.ServiceProvider.GetRequiredService<AiIncidentNoticePublisher>()
                .PublishPendingAsync(cancellationToken).ConfigureAwait(false);
        }
        return queued;
    }

    [LoggerMessage(9470, LogLevel.Error,
        "Agent spend notice reconciliation failed; pending notices will be retried")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
