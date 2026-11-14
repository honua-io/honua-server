// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Honua.Db.Postgres.Features.Attachments;

internal sealed partial class PostgresAttachmentMaintenanceService(
    IServiceScopeFactory scopeFactory, ILogger<PostgresAttachmentMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown interrupts both the timer and in-progress maintenance.
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<PostgresAttachmentMaintenance>()
                .RunOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            MaintenanceFailed(logger, exception);
        }
    }

    [LoggerMessage(EventId = 5590, Level = LogLevel.Warning, Message = "Attachment maintenance failed; durable work will be retried.")]
    private static partial void MaintenanceFailed(ILogger logger, Exception exception);
}
