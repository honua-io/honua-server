// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.TileOperations;

/// <summary>
/// Scheduled tile-cache expiry/invalidation service (#1837). On a configured cadence it dispatches
/// an <c>invalidate</c> tile operation for each configured target, so cached tiles for a tileset are
/// periodically refreshed. This is the time-based complement to the per-tileset <c>Cache-Control</c>
/// TTL shipped in #1794; it reuses the same <see cref="ITileOperationJobService" /> queue as the
/// startup <see cref="TileCacheWarmingHostedService" /> rather than introducing a parallel path.
/// </summary>
internal sealed partial class TileCacheExpiryHostedService(
    ITileOperationJobService jobService,
    IOptions<TileCacheExpiryOptions> options,
    ILogger<TileCacheExpiryHostedService> logger) : BackgroundService
{
    /// <summary>
    /// Minimum sweep interval. Sweeps queue invalidation jobs against the serving pod, so a too-low
    /// interval is clamped up to avoid self-inflicted invalidation storms.
    /// </summary>
    internal const int MinIntervalSeconds = 60;

    private readonly ITileOperationJobService _jobService = jobService ?? throw new ArgumentNullException(nameof(jobService));
    private readonly TileCacheExpiryOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<TileCacheExpiryHostedService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || _options.Targets.Count == 0)
        {
            return;
        }

        var intervalSeconds = Math.Max(MinIntervalSeconds, _options.IntervalSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
    }

    /// <summary>
    /// Runs a single expiry sweep: queues an <c>invalidate</c> tile operation for every configured
    /// target. Exposed internally so the behavior can be unit-tested without driving the timer.
    /// </summary>
    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        foreach (var target in _options.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!target.LayerId.HasValue && string.IsNullOrWhiteSpace(target.ServiceId))
            {
                Log.InvalidExpiryTargetSkipped(_logger);
                continue;
            }

            var request = new TileOperationStartRequest
            {
                Operation = "invalidate",
                ServiceId = target.ServiceId,
                LayerId = target.LayerId,
                TileMatrixSetId = target.TileMatrixSetId
            };

            var jobId = await _jobService.StartAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
            Log.ExpiryTargetQueued(_logger, jobId, target.ServiceId, target.LayerId);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 9242, Level = LogLevel.Information, Message = "Queued scheduled tile cache invalidate job {JobId} for service {ServiceId} layer {LayerId}.")]
        public static partial void ExpiryTargetQueued(ILogger logger, string jobId, string? serviceId, int? layerId);

        [LoggerMessage(EventId = 9243, Level = LogLevel.Warning, Message = "Skipped invalid scheduled tile cache expiry target (no service or layer).")]
        public static partial void InvalidExpiryTargetSkipped(ILogger logger);
    }
}
