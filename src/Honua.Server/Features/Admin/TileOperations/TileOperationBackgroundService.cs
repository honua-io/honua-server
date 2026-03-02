// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.TileOperations;

/// <summary>
/// Background worker that executes queued tile jobs.
/// </summary>
internal sealed class TileOperationBackgroundService(
    ITileOperationJobService tileOperationJobService,
    ILogger<TileOperationBackgroundService> logger) : BackgroundService
{
    private readonly ITileOperationJobService _tileOperationJobService = tileOperationJobService ?? throw new ArgumentNullException(nameof(tileOperationJobService));
    private readonly ILogger<TileOperationBackgroundService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in _tileOperationJobService.ReadQueuedJobIdsAsync(stoppingToken))
        {
            try
            {
                await _tileOperationJobService.ProcessQueuedJobAsync(jobId, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected exception while processing tile job {JobId}.", jobId);
            }
        }
    }
}

