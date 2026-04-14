// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Globalization;
using System.Text;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Configuration;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Validation;
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

        if (string.IsNullOrWhiteSpace(_options.Dispatch.DefaultWebhookSecret))
        {
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = false,
                Error = "Webhook signing secret is not configured."
            };
        }

        try
        {
            var destinationValidation = await OutboundHttpUrlValidator
                .ValidateAsync(destination, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!destinationValidation.IsValid || destinationValidation.Uri is null)
            {
                return new AlertDeliveryResult
                {
                    Succeeded = false,
                    Retryable = false,
                    Error = $"Webhook destination {destinationValidation.ErrorMessage ?? "must be a valid HTTPS URL."}"
                };
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, destinationValidation.Uri)
            {
                Content = new StringContent(alertEvent.PayloadJson, Encoding.UTF8, "application/json")
            };
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var signature = WebhookDeliveryHelper.ComputeSignature(_options.Dispatch.DefaultWebhookSecret, timestamp, alertEvent.PayloadJson);
            WebhookDeliveryHelper.AddValidatedHeader(request.Headers, "X-Honua-Alert-Rule", alertEvent.RuleId.ToString());
            WebhookDeliveryHelper.AddValidatedHeader(request.Headers, "X-Honua-Alert-Event", alertEvent.DedupeKey);
            WebhookDeliveryHelper.AddValidatedHeader(request.Headers, "X-Honua-Event-Timestamp", timestamp);
            WebhookDeliveryHelper.AddValidatedHeader(request.Headers, "X-Honua-Signature", $"sha256={signature}");
            WebhookDeliveryHelper.AddValidatedHeader(request.Headers, "Idempotency-Key", alertEvent.DedupeKey);

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
                Error = ex is HttpRequestException
                    ? "Webhook delivery failed."
                    : "Webhook delivery could not be completed."
            };
        }
    }
}
