// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Alerts;

/// <summary>
/// Thin adapter around <see cref="EventHubProducerClient"/> for testability.
/// </summary>
internal interface IEventHubPublisher : IAsyncDisposable
{
    Task<EventHubPublishResult> SendAsync(
        BinaryData body,
        Dictionary<string, object> properties,
        CancellationToken cancellationToken);
}

internal sealed record EventHubPublishResult(bool Succeeded, bool Retryable, string? Error);

internal sealed class EventHubPublisher : IEventHubPublisher
{
    private readonly EventHubProducerClient _client;

    public EventHubPublisher(EventHubProducerClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<EventHubPublishResult> SendAsync(
        BinaryData body,
        Dictionary<string, object> properties,
        CancellationToken cancellationToken)
    {
        try
        {
            var eventData = new EventData(body);

            foreach (var (key, value) in properties)
            {
                eventData.Properties[key] = value;
            }

            await _client.SendAsync([eventData], cancellationToken).ConfigureAwait(false);
            return new EventHubPublishResult(true, false, null);
        }
        catch (EventHubsException ex) when (ex.Reason == EventHubsException.FailureReason.ResourceNotFound)
        {
            return new EventHubPublishResult(false, false, "Event Hub not found.");
        }
        catch (EventHubsException ex) when (ex.IsTransient)
        {
            return new EventHubPublishResult(false, true, "Event Hub transient error.");
        }
        catch (UnauthorizedAccessException)
        {
            return new EventHubPublishResult(false, false, "Event Hub authorization failed.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed partial class AzureEventHubAlertDeliverySink : IAlertDeliverySink
{
    private readonly IEventHubPublisher _publisher;
    private readonly AlertDeliveryOptions _options;
    private readonly ILogger<AzureEventHubAlertDeliverySink> _logger;

    public AzureEventHubAlertDeliverySink(
        IEventHubPublisher publisher,
        IOptions<AlertDeliveryOptions> options,
        ILogger<AzureEventHubAlertDeliverySink> logger)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public AlertChannelType ChannelType => AlertChannelType.AzureEventHub;

    public async Task<AlertDeliveryResult> DeliverAsync(
        AlertDispatchItem dispatchItem,
        AlertEventEnvelope alertEvent,
        CancellationToken cancellationToken = default)
    {
        var ehOptions = _options.Dispatch.AzureEventHub;
        if (ehOptions is null || string.IsNullOrWhiteSpace(ehOptions.ConnectionString))
        {
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = false,
                Error = "Azure Event Hub connection string is not configured."
            };
        }

        try
        {
            var result = await _publisher.SendAsync(
                BinaryData.FromString(alertEvent.PayloadJson),
                AlertDeliveryMetadata.BuildObjectProperties(alertEvent),
                cancellationToken).ConfigureAwait(false);

            return new AlertDeliveryResult
            {
                Succeeded = result.Succeeded,
                Retryable = result.Retryable,
                Error = result.Error
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally generic: the Event Hub SDK surfaces a wide range of transport,
        // throttling, and auth exception types. Logged and converted to a non-throwing
        // AlertDeliveryResult so one sink failure never blocks other sinks.
        catch (Exception ex)
        {
            Log.DeliveryFailed(_logger, ex);
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = true,
                Error = "Event Hub alert delivery failed."
            };
        }
    }

    private static partial class Log
    {
        [LoggerMessage(10121, LogLevel.Warning, "Event Hub alert delivery failed with an unhandled exception.")]
        public static partial void DeliveryFailed(ILogger logger, Exception ex);
    }
}
