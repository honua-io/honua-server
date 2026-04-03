// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Events;

internal sealed partial class FeatureChangeRetryBackgroundService(
    IFeatureChangeRetryQueue retryQueue,
    ILogger<FeatureChangeRetryBackgroundService> logger) : BackgroundService
{
    private readonly IFeatureChangeRetryQueue _retryQueue = retryQueue ?? throw new ArgumentNullException(nameof(retryQueue));
    private readonly ILogger<FeatureChangeRetryBackgroundService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private static readonly TimeSpan WorkerRestartDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var pendingId in _retryQueue.ReadQueuedIdsAsync(stoppingToken).ConfigureAwait(false))
                {
                    try
                    {
                        await _retryQueue.ProcessQueuedAsync(pendingId, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        LogRetryWorkerFailed(_logger, pendingId, ex);
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
                LogRetryWorkerRestarting(_logger, ex);
                await Task.Delay(WorkerRestartDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(EventId = 9114, Level = LogLevel.Warning, Message = "Feature-change retry worker failed while processing {PendingId}.")]
    private static partial void LogRetryWorkerFailed(ILogger logger, string pendingId, Exception exception);

    [LoggerMessage(EventId = 9116, Level = LogLevel.Warning, Message = "Feature-change retry worker faulted and will restart.")]
    private static partial void LogRetryWorkerRestarting(ILogger logger, Exception exception);
}
