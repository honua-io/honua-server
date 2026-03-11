// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Azure;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Alerts;

internal sealed class AzureEventGridAlertDeliverySink : IAlertDeliverySink
{
    private readonly EventGridPublisherClient _client;
    private readonly AlertDeliveryOptions _options;

    public AzureEventGridAlertDeliverySink(
        EventGridPublisherClient client,
        IOptions<AlertDeliveryOptions> options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public AlertChannelType ChannelType => AlertChannelType.AzureEventGrid;

    public async Task<AlertDeliveryResult> DeliverAsync(
        AlertDispatchItem dispatchItem,
        AlertEventEnvelope alertEvent,
        CancellationToken cancellationToken = default)
    {
        var eventGridOptions = _options.Dispatch.AzureEventGrid;
        if (eventGridOptions is null || string.IsNullOrWhiteSpace(eventGridOptions.TopicEndpoint))
        {
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = false,
                Error = "Azure Event Grid topic endpoint is not configured."
            };
        }

        try
        {
            var cloudEvent = new CloudEvent(
                source: $"/honua/alerts/rules/{alertEvent.RuleId}",
                type: $"Honua.Alert.{alertEvent.TriggerType}",
                jsonSerializableData: BinaryData.FromString(alertEvent.PayloadJson))
            {
                Id = alertEvent.DedupeKey,
                Time = alertEvent.OccurredAt,
                Subject = $"layers/{alertEvent.LayerId}/features/{alertEvent.ObjectId}",
                ExtensionAttributes =
                {
                    ["honuaseverity"] = alertEvent.Severity.ToString().ToLowerInvariant(),
                    ["honuaruleid"] = alertEvent.RuleId.ToString()
                }
            };

            var response = await _client
                .SendEventAsync(cloudEvent, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsError)
            {
                return new AlertDeliveryResult { Succeeded = true, Retryable = false };
            }

            var retryable = response.Status >= 500 || response.Status == 429;
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = retryable,
                Error = $"Event Grid responded with {response.Status}: {response.ReasonPhrase}."
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RequestFailedException ex) when (ex.Status == 401 || ex.Status == 403)
        {
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = false,
                Error = $"Event Grid authorization failed: {ex.Message}"
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = false,
                Error = $"Event Grid topic not found: {ex.Message}"
            };
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
