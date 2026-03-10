// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;

namespace Honua.Server.Features.Alerts;

/// <summary>
/// In-process broadcaster for pushing alert events to connected subscribers.
/// WebSocket endpoint handlers subscribe to this broadcaster to receive real-time alerts.
/// </summary>
internal interface IAlertNotificationBroadcaster
{
    /// <summary>
    /// Broadcasts an alert event to all connected subscribers.
    /// </summary>
    Task BroadcastAsync(AlertEventEnvelope alertEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a subscriber callback. Dispose the returned handle to unsubscribe.
    /// </summary>
    IDisposable Subscribe(Func<AlertEventEnvelope, CancellationToken, Task> handler);
}

/// <summary>
/// Default in-memory implementation of the alert notification broadcaster.
/// Maintains a concurrent set of subscriber callbacks and fans out events.
/// </summary>
internal sealed class InMemoryAlertNotificationBroadcaster : IAlertNotificationBroadcaster
{
    private readonly ConcurrentDictionary<Guid, Func<AlertEventEnvelope, CancellationToken, Task>> _subscribers = new();

    public async Task BroadcastAsync(AlertEventEnvelope alertEvent, CancellationToken cancellationToken = default)
    {
        foreach (var subscriber in _subscribers.Values)
        {
            try
            {
                await subscriber(alertEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Individual subscriber failures do not block other subscribers.
            }
        }
    }

    public IDisposable Subscribe(Func<AlertEventEnvelope, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var id = Guid.NewGuid();
        _subscribers.TryAdd(id, handler);
        return new Subscription(this, id);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly InMemoryAlertNotificationBroadcaster _broadcaster;
        private readonly Guid _id;

        public Subscription(InMemoryAlertNotificationBroadcaster broadcaster, Guid id)
        {
            _broadcaster = broadcaster;
            _id = id;
        }

        public void Dispose() => _broadcaster._subscribers.TryRemove(_id, out _);
    }
}

/// <summary>
/// Delivery sink that pushes alert events to in-process WebSocket subscribers
/// via the <see cref="IAlertNotificationBroadcaster"/>.
/// </summary>
internal sealed class WebSocketAlertDeliverySink : IAlertDeliverySink
{
    private readonly IAlertNotificationBroadcaster _broadcaster;

    public WebSocketAlertDeliverySink(IAlertNotificationBroadcaster broadcaster)
    {
        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
    }

    public AlertChannelType ChannelType => AlertChannelType.WebSocket;

    public async Task<AlertDeliveryResult> DeliverAsync(
        AlertDispatchItem dispatchItem,
        AlertEventEnvelope alertEvent,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _broadcaster.BroadcastAsync(alertEvent, cancellationToken).ConfigureAwait(false);

            return new AlertDeliveryResult { Succeeded = true, Retryable = false };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = true,
                Error = ex.Message
            };
        }
    }
}
