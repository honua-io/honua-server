// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Alerts;

internal sealed class WebhookAlertDeliverySink : IAlertDeliverySink
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AlertOptions _options;

    public WebhookAlertDeliverySink(IHttpClientFactory httpClientFactory, IOptions<AlertOptions> options)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public AlertChannelType ChannelType => AlertChannelType.Webhook;

    public async Task<AlertDeliveryResult> DeliverAsync(
        AlertDispatchItem dispatchItem,
        AlertEventEnvelope alertEvent,
        CancellationToken cancellationToken = default)
    {
        var destination = dispatchItem.Destination;
        if (string.IsNullOrWhiteSpace(destination))
        {
            destination = _options.Dispatch.DefaultWebhookUrl;
        }

        if (string.IsNullOrWhiteSpace(destination))
        {
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = false,
                Error = "Webhook destination is not configured."
            };
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, destination)
            {
                Content = new StringContent(alertEvent.PayloadJson, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("X-Honua-Alert-Rule", alertEvent.RuleId.ToString());
            request.Headers.TryAddWithoutValidation("X-Honua-Alert-Event", alertEvent.DedupeKey);
            request.Headers.TryAddWithoutValidation("Idempotency-Key", alertEvent.DedupeKey);

            var client = _httpClientFactory.CreateClient("alerts-webhook");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return new AlertDeliveryResult
                {
                    Succeeded = true,
                    Retryable = false
                };
            }

            var retryable = (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests;
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = retryable,
                Error = $"Webhook responded with {(int)response.StatusCode}."
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
