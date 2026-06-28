// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Validation;
using Honua.Core.Features.Mobile.FieldCollection.Abstractions;
using Honua.Core.Features.Mobile.FieldCollection.Domain;
using Honua.Infrastructure.Events;

namespace Honua.Server.Features.Mobile.FieldCollection.Automations;

/// <summary>
/// Delivers FieldCollection automation webhooks (#2121). Builds a signed JSON
/// envelope describing the applied change and POSTs it to the action's configured
/// HTTPS endpoint, reusing the shared outbound-URL validation, HMAC signing, and
/// header sanitization used by the alert and feature-change webhook paths.
/// </summary>
internal sealed class WebhookFieldCollectionActionHandler : IFieldCollectionActionHandler
{
    internal const string HttpClientName = "fieldcollection-automation-webhook";
    internal const string UrlKey = "url";
    internal const string SecretKey = "secret";

    private readonly IHttpClientFactory _httpClientFactory;

    public WebhookFieldCollectionActionHandler(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    public FieldCollectionAutomationActionType ActionType => FieldCollectionAutomationActionType.Webhook;

    public async Task<FieldCollectionActionResult> ExecuteAsync(
        FieldCollectionActionInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var configuration = invocation.Action.Configuration;
        if (!configuration.TryGetValue(UrlKey, out var url) || string.IsNullOrWhiteSpace(url))
        {
            return FieldCollectionActionResult.Failure("Webhook 'url' is not configured.", retryable: false);
        }

        if (!configuration.TryGetValue(SecretKey, out var secret) || string.IsNullOrWhiteSpace(secret))
        {
            return FieldCollectionActionResult.Failure("Webhook signing 'secret' is not configured.", retryable: false);
        }

        var validation = await OutboundHttpUrlValidator
            .ValidateAsync(url, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.IsValid || validation.Uri is null)
        {
            return FieldCollectionActionResult.Failure(
                $"Webhook destination {validation.ErrorMessage ?? "must be a valid HTTPS URL."}",
                retryable: false);
        }

        var payload = BuildPayload(invocation.Event);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, validation.Uri)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var signature = WebhookDeliveryHelper.ComputeSignature(secret, timestamp, payload);
            WebhookDeliveryHelper.AddValidatedHeader(request.Headers, "X-Honua-FieldCollection-Action", invocation.Action.Id);
            WebhookDeliveryHelper.AddValidatedHeader(request.Headers, "X-Honua-Event-Timestamp", timestamp);
            WebhookDeliveryHelper.AddValidatedHeader(request.Headers, "X-Honua-Signature", $"sha256={signature}");
            WebhookDeliveryHelper.AddValidatedHeader(request.Headers, "Idempotency-Key", invocation.InvocationId);

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return FieldCollectionActionResult.Success();
            }

            var retryable = (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests;
            return FieldCollectionActionResult.Failure(
                $"Webhook responded with {(int)response.StatusCode}.",
                retryable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FieldCollectionActionResult.Failure(
                ex is HttpRequestException ? "Webhook delivery failed." : "Webhook delivery could not be completed.",
                retryable: true);
        }
    }

    internal static string BuildPayload(FieldCollectionAutomationEvent automationEvent)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("changeId", automationEvent.ChangeId);
            writer.WriteString("clientId", automationEvent.ClientId);
            writer.WriteString("featureId", automationEvent.FeatureId);
            writer.WriteNumber("layerId", automationEvent.LayerId);
            writer.WriteString("operation", OperationToWire(automationEvent.Operation));
            writer.WriteNumber("version", automationEvent.Version);
            writer.WriteNumber("generation", automationEvent.Generation);
            writer.WriteString("timestamp", automationEvent.Timestamp);

            if (automationEvent.FeaturePayloadJson is { Length: > 0 } featureJson)
            {
                writer.WritePropertyName("feature");
                using var document = JsonDocument.Parse(featureJson);
                document.RootElement.WriteTo(writer);
            }
            else
            {
                writer.WriteNull("feature");
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string OperationToWire(FieldCollectionChangeOperation operation) => operation switch
    {
        FieldCollectionChangeOperation.Insert => "insert",
        FieldCollectionChangeOperation.Update => "update",
        FieldCollectionChangeOperation.Delete => "delete",
        _ => "update",
    };
}
