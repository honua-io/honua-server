// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.TileOperations;

internal sealed partial class TileCacheWarmingHostedService(
    ITileOperationJobService jobService,
    IOptions<TileCacheWarmingOptions> options,
    ILogger<TileCacheWarmingHostedService> logger) : IHostedService
{
    private readonly ITileOperationJobService _jobService = jobService ?? throw new ArgumentNullException(nameof(jobService));
    private readonly TileCacheWarmingOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<TileCacheWarmingHostedService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || _options.Targets.Count == 0)
        {
            return;
        }

        foreach (var target in _options.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var operation = string.IsNullOrWhiteSpace(target.Operation) ? "warm" : target.Operation.Trim().ToLowerInvariant();
            if (operation is not ("seed" or "warm"))
            {
                Log.InvalidWarmTargetSkipped(_logger, operation);
                continue;
            }

            if (!target.LayerId.HasValue && string.IsNullOrWhiteSpace(target.ServiceId))
            {
                Log.InvalidWarmTargetSkipped(_logger, operation);
                continue;
            }

            var request = new TileOperationStartRequest
            {
                Operation = operation,
                ServiceId = target.ServiceId,
                LayerId = target.LayerId,
                MinZoom = target.MinZoom,
                MaxZoom = target.MaxZoom,
                TileMatrixSetId = target.TileMatrixSetId,
                Bbox = target.Bbox,
                MaxTiles = target.MaxTiles
            };

            var jobId = await _jobService.StartAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
            Log.WarmTargetQueued(_logger, jobId, operation, target.ServiceId, target.LayerId, target.MinZoom, target.MaxZoom);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static partial class Log
    {
        [LoggerMessage(EventId = 9240, Level = LogLevel.Information, Message = "Queued tile cache {Operation} job {JobId} for service {ServiceId} layer {LayerId} zoom {MinZoom}-{MaxZoom}.")]
        public static partial void WarmTargetQueued(ILogger logger, string jobId, string operation, string? serviceId, int? layerId, int? minZoom, int? maxZoom);

        [LoggerMessage(EventId = 9241, Level = LogLevel.Warning, Message = "Skipped invalid tile cache warm target for operation {Operation}.")]
        public static partial void InvalidWarmTargetSkipped(ILogger logger, string operation);
    }
}
