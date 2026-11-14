// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;

namespace Honua.Infrastructure.Monitoring;

internal sealed partial class OpsNotificationReminderService(
    IServiceScopeFactory scopeFactory,
    IExecutionJobStore jobStore,
    TimeProvider timeProvider,
    ILogger<OpsNotificationReminderService> logger) : BackgroundService
{
    private const string LeaseId = "ops-notification-reminders";
    private readonly string _ownerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                ReminderIterationFailed(logger, exception);
            }
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task EvaluateOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!await jobStore.TryAcquireLeaseAsync(LeaseId, _ownerId, TimeSpan.FromMinutes(5), cancellationToken)
            .ConfigureAwait(false))
        {
            return;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(1));
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<OpsNotificationReminderRunner>()
                .RunAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            await jobStore.ReleaseLeaseAsync(LeaseId, _ownerId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    [LoggerMessage(9498, LogLevel.Warning, "Ops notification reminder iteration failed")]
    private static partial void ReminderIterationFailed(ILogger logger, Exception exception);
}
