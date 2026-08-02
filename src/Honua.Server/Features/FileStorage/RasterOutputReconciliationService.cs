// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Raster;
using Microsoft.Extensions.Options;

namespace Honua.FileStorage;

internal sealed class RasterOutputReconciliationScheduledTickHandler(
    RasterOutputReconciliationService service) : IScheduledTickHandler
{
    public ScheduledTickKind Kind => ScheduledTickKind.RasterOutputReconciliation;

    public Task RunTickAsync(CancellationToken cancellationToken = default) =>
        service.RunSweepAsync(cancellationToken);
}

/// <summary>Runs bounded, lease-fenced cleanup of expired raster output objects.</summary>
internal sealed partial class RasterOutputReconciliationService(
    IServiceScopeFactory serviceScopeFactory,
    IOptionsMonitor<RasterOutputPublicationOptions> options,
    ILogger<RasterOutputReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.CurrentValue.ReconciliationInterval, stoppingToken)
                    .ConfigureAwait(false);
                await RunSweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Log.SweepFailed(logger, exception);
            }
        }
    }

    internal async Task RunSweepAsync(CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        if (current.OrphanGracePeriod <= TimeSpan.Zero
            || current.ReconciliationInterval <= TimeSpan.Zero
            || current.MaximumSweepCount <= 0)
        {
            throw new InvalidOperationException("Raster output reconciliation options are invalid.");
        }

        using var scope = serviceScopeFactory.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<RasterOutputPublisher>();
        var result = await publisher.SweepOrphansAsync(
            DateTimeOffset.UtcNow.Subtract(current.OrphanGracePeriod),
            current.MaximumSweepCount,
            cancellationToken).ConfigureAwait(false);
        if (result.Deleted > 0 || result.RetainedVisible > 0)
        {
            Log.SweepCompleted(logger, result.Inspected, result.Deleted, result.RetainedVisible);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(7720, LogLevel.Information,
            "Raster output orphan sweep inspected {Inspected}, deleted {Deleted}, and retained {RetainedVisible} visible objects")]
        public static partial void SweepCompleted(
            ILogger logger,
            int inspected,
            int deleted,
            int retainedVisible);

        [LoggerMessage(7721, LogLevel.Warning, "Raster output orphan sweep failed")]
        public static partial void SweepFailed(ILogger logger, Exception exception);
    }
}
