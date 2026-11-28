// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Monitoring;

internal sealed partial class SupportTelemetryPushBackgroundService(
    SupportTelemetryPushClient pushClient,
    IOptionsMonitor<SupportTelemetryPushOptions> optionsMonitor,
    ILogger<SupportTelemetryPushBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = optionsMonitor.CurrentValue.EffectiveInterval;
            try
            {
                await pushClient.PushIngestAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                PushFailed(logger, exception);
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    [LoggerMessage(EventId = 8652, Level = LogLevel.Warning, Message = "Support telemetry push failed; will retry on the next configured interval.")]
    private static partial void PushFailed(ILogger logger, Exception exception);
}
