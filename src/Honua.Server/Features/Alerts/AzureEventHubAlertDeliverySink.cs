// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Alerts;

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
            return new EventHubPublishResult(false, false, $"Event Hub not found: {ex.Message}");
        }
        catch (EventHubsException ex) when (ex.IsTransient)
        {
            return new EventHubPublishResult(false, true, $"Event Hub transient error: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new EventHubPublishResult(false, false, $"Event Hub authorization failed: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class AzureEventHubAlertDeliverySink : IAlertDeliverySink
{
    private readonly IEventHubPublisher _publisher;
    private readonly AlertDeliveryOptions _options;

    public AzureEventHubAlertDeliverySink(
        IEventHubPublisher publisher,
        IOptions<AlertDeliveryOptions> options)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
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
