// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.TileOperations;

internal sealed class TileCacheExpiryScheduledTickHandler(TileCacheExpiryHostedService service)
    : Honua.Core.Features.ControlPlane.Abstractions.IScheduledTickHandler
{
    public Honua.Core.Features.ControlPlane.Abstractions.ScheduledTickKind Kind
        => Honua.Core.Features.ControlPlane.Abstractions.ScheduledTickKind.TileCacheExpiry;

    public Task RunTickAsync(CancellationToken cancellationToken = default)
        => service.SweepAsync(cancellationToken);
}

internal sealed partial class TileCacheExpiryHostedService(
    ITileOperationJobService jobService,
    IOptions<TileCacheExpiryOptions> options,
    ILogger<TileCacheExpiryHostedService> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    internal const int MinIntervalSeconds = 60;

    private readonly ITileOperationJobService _jobService = jobService ?? throw new ArgumentNullException(nameof(jobService));
    private readonly TileCacheExpiryOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<TileCacheExpiryHostedService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || _options.Targets.Count == 0)
        {
            return;
        }

        var intervalSeconds = Math.Max(MinIntervalSeconds, _options.IntervalSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds), _timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await SweepAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                // A Redis or progress-store fault must not stop the host. Log and retry next tick.
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.ExpirySweepFailed(_logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
    }

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

        [LoggerMessage(EventId = 9244, Level = LogLevel.Warning, Message = "Scheduled tile-cache expiry sweep failed; will retry on the next tick.")]
        public static partial void ExpirySweepFailed(ILogger logger, Exception exception);
    }
}
