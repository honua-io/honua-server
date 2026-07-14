// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.TileOperations;

/// <summary>
/// Adapts the live LRU tile-cache eviction sweep to the control-plane scheduled-tick dispatcher so
/// EventBridge Scheduler can drive one sweep under <c>TriggerMode=Event</c> without hosting the
/// in-process timer. Routes to <see cref="TileCacheEvictionService.SweepAsync" /> (the same body the
/// timer calls), no-op when eviction is disabled. Re-running just re-evaluates current state.
/// </summary>
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

/// <summary>
/// Background service that periodically runs the live size-quota / LRU tile-cache evictor (#1917).
/// On each tick it asks <see cref="TileCacheEvictionService" /> to snapshot the Redis tile-key index
/// and evict the least-recently-used tiles that exceed the configured quotas. This is the live
/// binding of the LRU evictor onto the hot-path tile-key store; it supersedes relying solely on the
/// Redis server <c>maxmemory-policy allkeys-lru</c> setting, which cannot honor Honua's per-deployment
/// entry-count and byte-size quotas. Disabled unless <c>TileOptions:Eviction:Enabled</c> is set.
/// </summary>
internal sealed partial class TileCacheEvictionHostedService(
    TileCacheEvictionService evictionService,
    IOptions<TileCacheEvictionSweepOptions> options,
    ILogger<TileCacheEvictionHostedService> logger) : BackgroundService
{
    /// <summary>
    /// Minimum sweep interval. Sweeps scan the whole key index, so a too-low interval is clamped up
    /// to avoid hammering Redis on the serving pod.
    /// </summary>
    internal const int MinIntervalSeconds = 30;

    private readonly TileCacheEvictionService _evictionService = evictionService ?? throw new ArgumentNullException(nameof(evictionService));
    private readonly TileCacheEvictionSweepOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<TileCacheEvictionHostedService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
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
                catch (Exception ex)
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

/// <summary>
/// Configuration for the scheduled tile-cache eviction sweep cadence (#1917). The quota itself
/// (enabled flag, entry-count and byte-size caps) lives in
/// <see cref="Honua.Core.Features.Tiles.TileCacheEvictionOptions" /> under <c>TileOptions:Eviction</c>;
/// this only controls how often the background sweep runs.
/// </summary>
internal sealed class TileCacheEvictionSweepOptions
{
    /// <summary>The configuration section name that binds to these options.</summary>
    public const string SectionName = "TileOptions:EvictionSweep";

    /// <summary>
    /// How often the evictor sweeps the tile-key index, in seconds. Values below
    /// <see cref="TileCacheEvictionHostedService.MinIntervalSeconds" /> are clamped up. Defaults to
    /// five minutes.
    /// </summary>
    public int IntervalSeconds { get; set; } = 300;
}
