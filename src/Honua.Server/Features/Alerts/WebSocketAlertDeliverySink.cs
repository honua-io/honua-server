// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Server.Features.Infrastructure.Abstractions;

namespace Honua.Server.Features.Alerts;

/// <summary>
/// Options for creating a subscription with metadata.
/// </summary>
internal sealed record SubscriptionOptions(string? ClientLabel = null);

/// <summary>
/// Handle for a live alert notification subscription.
/// </summary>
internal interface IAlertNotificationSubscription : IDisposable
{
    Guid SubscriberId { get; }

    CancellationToken DisconnectToken { get; }
}

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
    IAlertNotificationSubscription Subscribe(Func<AlertEventEnvelope, CancellationToken, Task> handler);

    /// <summary>
    /// Registers a subscriber callback with metadata. Dispose the returned handle to unsubscribe.
    /// </summary>
    IAlertNotificationSubscription Subscribe(Func<AlertEventEnvelope, CancellationToken, Task> handler, SubscriptionOptions? options);
}

/// <summary>
/// Default in-memory implementation of the alert notification broadcaster.
/// Maintains a concurrent set of subscriber callbacks and fans out events.
/// </summary>
internal sealed class InMemoryAlertNotificationBroadcaster : IAlertNotificationBroadcaster, IStreamingSubscriptionManager
{
    private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _subscribers = new();

    public async Task BroadcastAsync(AlertEventEnvelope alertEvent, CancellationToken cancellationToken = default)
    {
        foreach (var entry in _subscribers.Values)
        {
            if (entry.Cts.IsCancellationRequested)
            {
                continue;
            }

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, entry.Cts.Token);
                await entry.Handler(alertEvent, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // Individual subscriber was disconnected; skip.
            }
            catch
            {
                // Individual subscriber failures do not block other subscribers.
            }
        }
    }

    public IAlertNotificationSubscription Subscribe(Func<AlertEventEnvelope, CancellationToken, Task> handler)
        => Subscribe(handler, null);

    public IAlertNotificationSubscription Subscribe(Func<AlertEventEnvelope, CancellationToken, Task> handler, SubscriptionOptions? options)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var id = Guid.NewGuid();
        var entry = new SubscriptionEntry(handler, new CancellationTokenSource(), DateTimeOffset.UtcNow, options?.ClientLabel);
        _subscribers.TryAdd(id, entry);
        return new Subscription(this, id, entry.Cts.Token);
    }

    public IReadOnlyList<StreamingSubscriptionInfo> GetSubscriptions()
    {
        return _subscribers.Select(kvp =>
            new StreamingSubscriptionInfo(kvp.Key, kvp.Value.ConnectedAt, kvp.Value.ClientLabel)).ToArray();
    }

    public bool DisconnectSubscriber(Guid subscriberId)
    {
        if (!_subscribers.TryRemove(subscriberId, out var entry))
        {
            return false;
        }

        entry.Cts.Cancel();
        entry.Cts.Dispose();
        return true;
    }

    private void RemoveSubscriber(Guid id)
    {
        if (_subscribers.TryRemove(id, out var entry))
        {
            entry.Cts.Cancel();
            entry.Cts.Dispose();
        }
    }

    private sealed record SubscriptionEntry(
        Func<AlertEventEnvelope, CancellationToken, Task> Handler,
        CancellationTokenSource Cts,
        DateTimeOffset ConnectedAt,
        string? ClientLabel);

    private sealed class Subscription : IAlertNotificationSubscription
    {
        private readonly InMemoryAlertNotificationBroadcaster _broadcaster;
        private readonly Guid _id;
        private readonly CancellationToken _disconnectToken;

        public Subscription(InMemoryAlertNotificationBroadcaster broadcaster, Guid id, CancellationToken disconnectToken)
        {
            _broadcaster = broadcaster;
            _id = id;
            _disconnectToken = disconnectToken;
        }

        public Guid SubscriberId => _id;

        public CancellationToken DisconnectToken => _disconnectToken;

        public void Dispose() => _broadcaster.RemoveSubscriber(_id);
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
