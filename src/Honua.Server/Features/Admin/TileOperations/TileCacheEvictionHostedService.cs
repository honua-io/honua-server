// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.TileOperations;

internal sealed class TileCacheEvictionScheduledTickHandler(TileCacheEvictionService evictionService)
    : Honua.Core.Features.ControlPlane.Abstractions.IScheduledTickHandler
{
    public Honua.Core.Features.ControlPlane.Abstractions.ScheduledTickKind Kind
        => Honua.Core.Features.ControlPlane.Abstractions.ScheduledTickKind.TileCacheEviction;

    public Task RunTickAsync(CancellationToken cancellationToken = default)
        => evictionService.IsEnabled
            ? evictionService.SweepAsync(cancellationToken)
            : Task.CompletedTask;
}

// Eviction honors the deployment's entry and byte quotas and stays off unless it is enabled, because the Redis eviction policy cannot express those quotas.
internal sealed partial class TileCacheEvictionHostedService(
    TileCacheEvictionService evictionService,
    IOptions<TileCacheEvictionSweepOptions> options,
    ILogger<TileCacheEvictionHostedService> logger) : BackgroundService
{
    internal const int MinIntervalSeconds = 30;

    private readonly TileCacheEvictionService _evictionService = evictionService ?? throw new ArgumentNullException(nameof(evictionService));
    private readonly TileCacheEvictionSweepOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<TileCacheEvictionHostedService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_evictionService.IsEnabled)
        {
            return;
        }

        var intervalSeconds = Math.Max(MinIntervalSeconds, _options.IntervalSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _evictionService.SweepAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                // Intentionally generic: this is a periodic background eviction sweep. A
                // failed sweep must not tear down the hosted service; log and retry next tick.
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.EvictionSweepFailed(_logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 9265, Level = LogLevel.Warning, Message = "Scheduled tile-cache eviction sweep failed; will retry on the next tick.")]
        public static partial void EvictionSweepFailed(ILogger logger, Exception exception);
    }
}

internal sealed class TileCacheEvictionSweepOptions
{
    public const string SectionName = "TileOptions:EvictionSweep";

    public int IntervalSeconds { get; set; } = 300;
}
