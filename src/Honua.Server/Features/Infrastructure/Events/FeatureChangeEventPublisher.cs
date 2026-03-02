// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Threading.Channels;

namespace Honua.Server.Features.Infrastructure.Events;

/// <summary>
/// Internal queue for feature-change events awaiting webhook delivery.
/// </summary>
internal interface IFeatureChangeEventQueue
{
    ValueTask EnqueueAsync(FeatureChangeEvent featureEvent, CancellationToken cancellationToken = default);
    IAsyncEnumerable<FeatureChangeEvent> ReadAllAsync(CancellationToken cancellationToken = default);
}

internal sealed class FeatureChangeEventQueue : IFeatureChangeEventQueue
{
    private readonly Channel<FeatureChangeEvent> _channel = Channel.CreateUnbounded<FeatureChangeEvent>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    public ValueTask EnqueueAsync(FeatureChangeEvent featureEvent, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(featureEvent, cancellationToken);
    }

    public IAsyncEnumerable<FeatureChangeEvent> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        return _channel.Reader.ReadAllAsync(cancellationToken);
    }
}

/// <summary>
/// Persists and fans out feature-change notifications.
/// </summary>
internal sealed partial class FeatureChangeEventPublisher(
    IFeatureChangeEventStore store,
    IFeatureChangeEventQueue queue,
    ILogger<FeatureChangeEventPublisher> logger) : IFeatureChangeEventPublisher
{
    private readonly IFeatureChangeEventStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IFeatureChangeEventQueue _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    private readonly ILogger<FeatureChangeEventPublisher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task PublishAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var stored = await _store.AppendAsync(request, cancellationToken).ConfigureAwait(false);
            await _queue.EnqueueAsync(stored, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogPublishFailed(_logger, ex);
        }
    }

    [LoggerMessage(EventId = 9100, Level = LogLevel.Warning, Message = "Failed to publish feature-change event.")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception);
}

