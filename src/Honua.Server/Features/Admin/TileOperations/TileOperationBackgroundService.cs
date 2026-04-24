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
    private static readonly TimeSpan WorkerRestartDelay = TimeSpan.FromSeconds(5);
    private readonly ITileOperationJobService _tileOperationJobService = tileOperationJobService ?? throw new ArgumentNullException(nameof(tileOperationJobService));
    private readonly ILogger<TileOperationBackgroundService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var jobId in _tileOperationJobService.ReadQueuedJobIdsAsync(stoppingToken))
                {
                    try
                    {
                        await _tileOperationJobService.ProcessQueuedJobAsync(jobId, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        TileOperationLog.BackgroundJobProcessingFailed(_logger, jobId, ex);
                    }
                }

                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                TileOperationLog.BackgroundWorkerRestarting(_logger, ex);
                await Task.Delay(WorkerRestartDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
