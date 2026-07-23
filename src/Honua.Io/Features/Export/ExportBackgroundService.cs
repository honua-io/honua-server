// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Io.Export;

/// <summary>
/// Background service that processes queued export jobs for large datasets.
/// </summary>
internal sealed class ExportBackgroundService : BackgroundService
{
    private static readonly TimeSpan WorkerRestartDelay = TimeSpan.FromSeconds(5);
    private readonly IExportJobService _exportJobService;
    private readonly ILogger<ExportBackgroundService> _logger;

    public ExportBackgroundService(
        IExportJobService exportJobService,
        ILogger<ExportBackgroundService> logger)
    {
        _exportJobService = exportJobService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var jobId in _exportJobService.ReadQueuedJobIdsAsync(stoppingToken).ConfigureAwait(false))
                {
                    try
                    {
                        await _exportJobService.ProcessQueuedJobAsync(jobId, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    // Intentionally generic: this is a long-running background loop; a single
                    // failed job must not kill the host or the queue-reading loop — log and
                    // keep processing the next queued job.
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        ExportLog.AsyncExportFailed(_logger, jobId, ex);
                    }
                }

                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            // Intentionally generic: this is the outer restart boundary for the whole
            // background worker loop; a failure while reading the job queue itself (as
            // opposed to a single job) must not kill the host — log, back off, and retry.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ExportLog.BackgroundWorkerRestarting(_logger, ex);
                await Task.Delay(WorkerRestartDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
