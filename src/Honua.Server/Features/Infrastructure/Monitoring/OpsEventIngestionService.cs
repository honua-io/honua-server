// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

internal sealed partial class OpsEventIngestionService(
    IOpsEventQueueClient queue,
    IServiceScopeFactory scopeFactory,
    ILogger<OpsEventIngestionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var messages = await queue.ReceiveAsync(stoppingToken).ConfigureAwait(false);
                foreach (var message in messages)
                {
                    try
                    {
                        await ProcessAsync(message, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Leave failed messages visible for retry and the queue's configured DLQ redrive.
                        MessageFailed(logger, exception);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                QueueFailed(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task ProcessAsync(OpsEventQueueMessage message, CancellationToken cancellationToken)
    {
        var externalEvent = OpsExternalEventMapper.Parse(message.Body);
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOpsExternalEventStore>();
        await store.AppendAsync(externalEvent, cancellationToken).ConfigureAwait(false);
        foreach (var handler in scope.ServiceProvider.GetServices<IOpsExternalEventHandler>())
        {
            await handler.HandleAsync(externalEvent, cancellationToken).ConfigureAwait(false);
        }

        await queue.DeleteAsync(message.ReceiptHandle, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 8430, Level = LogLevel.Warning, Message = "Ops event was not acknowledged; queue retry and dead-letter policy remain in effect.")]
    private static partial void MessageFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 8431, Level = LogLevel.Warning, Message = "Ops event queue receive failed; retrying after a bounded delay.")]
    private static partial void QueueFailed(ILogger logger, Exception exception);
}
