// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Realtime;
using Honua.Infrastructure.Abstractions;
using Honua.Server.Features.Admin.Models;

namespace Honua.Alerts;

internal sealed partial class RealtimeAlertNotificationBroadcaster(
    IRealtimeFanoutProvider provider,
    ILogger<RealtimeAlertNotificationBroadcaster> logger)
    : IStreamingSubscriptionManager, IDisposable, IAsyncDisposable
{
    private readonly IRealtimeFanoutProvider _provider = provider;
    private readonly ILogger<RealtimeAlertNotificationBroadcaster> _logger = logger;
    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();
    private readonly Lock _gate = new();
    private bool _disposed;

    public async Task BroadcastAsync(AlertEventEnvelope alertEvent, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToElement(alertEvent, StreamingOperationsJsonContext.Default.AlertEventEnvelope);
        await _provider.AppendAsync(
            new RealtimeEventDraft(RealtimeFamily.Alerts, alertEvent.ServiceId, "alert", payload),
            null, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IAlertNotificationSubscription> SubscribeAsync(
        Func<AlertEventEnvelope, RealtimePosition, CancellationToken, Task> handler,
        SubscriptionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        // Capture the live boundary before returning so events published immediately
        // after registration are replayed even if the provider starts its pump later.
        var window = await _provider.GetWindowAsync(RealtimeFamily.Alerts, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var subscription = new Subscription(this, handler, options);
            _subscriptions.TryAdd(subscription.SubscriberId, subscription);
            try
            {
                subscription.Start(options?.After ?? window.Head);
            }
            catch
            {
                _subscriptions.TryRemove(subscription.SubscriberId, out _);
                subscription.Dispose();
                throw;
            }
            return subscription;
        }
    }

    public IReadOnlyList<StreamingSubscriptionInfo> GetSubscriptions()
        => _subscriptions.Values.Where(subscription => !subscription.DisconnectToken.IsCancellationRequested)
            .Select(subscription => subscription.Info).ToArray();

    public bool DisconnectSubscriber(Guid subscriberId)
    {
        if (!_subscriptions.TryGetValue(subscriberId, out var subscription) || subscription.DisconnectToken.IsCancellationRequested)
        {
            return false;
        }

        subscription.Dispose();
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var subscription in _subscriptions.Values)
            {
                subscription.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pumps;
        lock (_gate)
        {
            pumps = _subscriptions.Values.Select(subscription => subscription.Completion).ToArray();
            Dispose();
        }

        await Task.WhenAll(pumps).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "An alert realtime subscription closed after a delivery failure.")]
    private static partial void LogSubscriptionFailure(ILogger logger, Exception exception);

    private sealed class Subscription : IAlertNotificationSubscription
    {
        private const int DeliveryCapacity = 64;
        private readonly RealtimeAlertNotificationBroadcaster _owner;
        private readonly Func<AlertEventEnvelope, RealtimePosition, CancellationToken, Task> _handler;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Channel<RealtimeDelivery> _deliveries = Channel.CreateBounded<RealtimeDelivery>(DeliveryCapacity);
        private readonly TaskCompletionSource _sourceClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public Subscription(RealtimeAlertNotificationBroadcaster owner,
            Func<AlertEventEnvelope, RealtimePosition, CancellationToken, Task> handler, SubscriptionOptions? options)
        {
            _owner = owner;
            _handler = handler;
            Info = new StreamingSubscriptionInfo(Guid.NewGuid(), DateTimeOffset.UtcNow, options?.ClientLabel);
            DisconnectToken = _cancellation.Token;
        }

        public StreamingSubscriptionInfo Info { get; }
        public Guid SubscriberId => Info.SubscriberId;
        public CancellationToken DisconnectToken { get; }
        public RealtimeDelivery.Reset? Reset { get; private set; }
        public Task Completion { get; private set; } = Task.CompletedTask;

        public void Start(long after)
        {
            var source = _owner._provider.Subscribe(new RealtimeSubscriptionRequest(
                RealtimeFamily.Alerts, [], after, new SourceWriter(_deliveries.Writer, _sourceClosed)));
            Completion = RunAsync(source);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _cancellation.Cancel();
            }
        }

        private async Task ObserveSourceClosureAsync()
        {
            try
            {
                await _sourceClosed.Task.WaitAsync(DisconnectToken).ConfigureAwait(false);
                Dispose();
            }
            catch (OperationCanceledException) when (DisconnectToken.IsCancellationRequested)
            {
            }
        }

        private async Task RunAsync(IRealtimeSubscription source)
        {
            var observeClosure = ObserveSourceClosureAsync();
            try
            {
                await using var sourceLifetime = source;
                await foreach (var delivery in _deliveries.Reader.ReadAllAsync(DisconnectToken).ConfigureAwait(false))
                {
                    if (delivery is RealtimeDelivery.Reset reset)
                    {
                        Reset = reset;
                        break;
                    }

                    if (delivery is RealtimeDelivery.Event { Value.Kind: "alert" } notification)
                    {
                        var alert = notification.Value.Payload.Deserialize(StreamingOperationsJsonContext.Default.AlertEventEnvelope)
                            ?? throw new JsonException("An alert event envelope is required.");
                        await _handler(alert, notification.Value.Position, DisconnectToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (DisconnectToken.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A failed or overflowing subscriber closes without interrupting other streams.
                LogSubscriptionFailure(_owner._logger, exception);
            }
            finally
            {
                Dispose();
                _owner._subscriptions.TryRemove(SubscriberId, out _);
                await observeClosure.ConfigureAwait(false);
                _cancellation.Dispose();
            }
        }
        // Reader.Completion waits for buffered deliveries to drain. Observe the
        // producer's close directly so overflow cancels even a blocked handler.
        private sealed class SourceWriter(ChannelWriter<RealtimeDelivery> writer, TaskCompletionSource closed)
            : ChannelWriter<RealtimeDelivery>
        {
            public override bool TryWrite(RealtimeDelivery item) => writer.TryWrite(item);

            public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
                => writer.WaitToWriteAsync(cancellationToken);

            public override bool TryComplete(Exception? error = null)
            {
                var completed = writer.TryComplete(error);
                if (error is not null)
                {
                    closed.TrySetResult();
                }
                return completed;
            }
        }
    }
}
