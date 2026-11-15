// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Admission;

namespace Honua.Alerts.Ops;

internal sealed partial class AiMonthlyActorNoticeReconciler(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<AiMonthlyActorNoticeReconciler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogPassFailed(logger, exception);
            }

            try
            {
                await Task.Delay(Interval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    internal async Task<int> ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<IAiMonthlyActorNoticeStore>() is null)
        {
            return 0;
        }

        return await scope.ServiceProvider.GetRequiredService<AiMonthlyActorNoticePublisher>()
            .PublishPendingAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(9470, LogLevel.Error,
        "Monthly agent spend notice reconciliation failed; pending notices will be retried")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
