// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Monitoring;

internal sealed partial class OpsWatchdogStartupReconciliation(
    IServiceScopeFactory scopeFactory,
    IOptions<OpsAutonomyOptions> options,
    ILogger<OpsWatchdogStartupReconciliation> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var writer = scope.ServiceProvider.GetRequiredService<OpsAutonomySettingsWriter>();
                await writer.ReconcileAsync(
                    new OpsAutonomySettings { KillSwitchEnabled = options.Value.KillSwitchEnabled },
                    stoppingToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                ReconciliationFailed(logger, exception);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(10323, LogLevel.Warning, "Watchdog startup stop reconciliation failed; retrying")]
    private static partial void ReconciliationFailed(ILogger logger, Exception exception);
}
