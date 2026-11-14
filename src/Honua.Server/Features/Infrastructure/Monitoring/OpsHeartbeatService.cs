// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Monitoring;

internal sealed partial class OpsHeartbeatService(
    OpsHeartbeatCoordinator coordinator,
    IConfiguration configuration,
    ILogger<OpsHeartbeatService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var stackName = configuration["Observability:Heartbeat:StackName"]!;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await coordinator.PublishAsync(stackName, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                PublicationFailed(logger, exception);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(EventId = 8432, Level = LogLevel.Warning, Message = "Operations heartbeat could not be published; the external missing-data alarm remains authoritative.")]
    private static partial void PublicationFailed(ILogger logger, Exception exception);
}
