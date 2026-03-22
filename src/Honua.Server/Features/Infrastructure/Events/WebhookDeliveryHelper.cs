// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.Server.Features.Infrastructure.Events;

/// <summary>
/// Shared webhook delivery logic: HMAC signature, signed HTTP POST, and retry with exponential backoff.
/// Used by both the feature-change and manifest-approval webhook dispatchers.
/// </summary>
internal static partial class WebhookDeliveryHelper
{
    /// <summary>
    /// Computes an HMAC-SHA256 signature for webhook payloads using the <c>timestamp.payload</c> message format.
    /// Delegates to <see cref="WebhookSignatureHelper"/> to avoid duplication.
    /// </summary>
    internal static string ComputeSignature(string secret, string timestamp, string payload)
        => WebhookSignatureHelper.ComputeSignature(secret, timestamp, payload);

    /// <summary>
    /// Delivers a signed JSON payload to a webhook endpoint with retry and exponential backoff.
    /// </summary>
    internal static async Task<bool> DeliverWithRetryAsync(
        WebhookDeliveryRequest request,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var timestamp = request.Timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = ComputeSignature(request.Secret, timestamp, request.Payload);
        var maxAttempts = Math.Max(1, request.MaxAttempts);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, request.RequestTimeoutSeconds)));

                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, request.WebhookUri)
                {
                    Content = new StringContent(request.Payload, Encoding.UTF8, "application/json")
                };
                httpRequest.Headers.TryAddWithoutValidation("X-Honua-Event-Id", request.EventId);
                httpRequest.Headers.TryAddWithoutValidation("X-Honua-Event-Timestamp", timestamp);
                httpRequest.Headers.TryAddWithoutValidation("X-Honua-Signature", $"sha256={signature}");
                httpRequest.Headers.TryAddWithoutValidation("Idempotency-Key", request.EventId);

                var client = httpClientFactory.CreateClient(request.HttpClientName);
                using var response = await client.SendAsync(httpRequest, timeoutCts.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    LogDeliverySucceeded(logger, request.EventId, response.StatusCode);
                    return true;
                }

                var isRetryable = (int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests;
                LogDeliveryFailed(logger, request.EventId, attempt, (int)response.StatusCode, isRetryable);
                if (!isRetryable || attempt == maxAttempts)
                {
                    return false;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogDeliveryException(logger, request.EventId, attempt, ex);
                if (attempt == maxAttempts)
                {
                    return false;
                }
            }

            var delayMs = Math.Min(
                Math.Max(1, request.InitialBackoffMs) * (1 << (attempt - 1)),
                Math.Max(1, request.MaxBackoffMs));
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    [LoggerMessage(EventId = 9001, Level = LogLevel.Debug, Message = "Webhook delivery succeeded for event {EventId} with status {StatusCode}.")]
    private static partial void LogDeliverySucceeded(ILogger logger, string eventId, System.Net.HttpStatusCode statusCode);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Warning, Message = "Webhook delivery failed for event {EventId} on attempt {Attempt} with status {StatusCode}. Retryable={Retryable}.")]
    private static partial void LogDeliveryFailed(ILogger logger, string eventId, int attempt, int statusCode, bool retryable);

    [LoggerMessage(EventId = 9003, Level = LogLevel.Warning, Message = "Webhook delivery threw for event {EventId} on attempt {Attempt}.")]
    private static partial void LogDeliveryException(ILogger logger, string eventId, int attempt, Exception exception);
}

/// <summary>
/// Parameters for a webhook delivery attempt.
/// </summary>
internal readonly record struct WebhookDeliveryRequest
{
    /// <summary>
    /// Pre-serialized JSON payload.
    /// </summary>
    public required string Payload { get; init; }

    /// <summary>
    /// Event identifier used for headers and idempotency.
    /// </summary>
    public required string EventId { get; init; }

    /// <summary>
    /// Event timestamp used for signature generation.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Target webhook URI.
    /// </summary>
    public required Uri WebhookUri { get; init; }

    /// <summary>
    /// HMAC shared secret.
    /// </summary>
    public required string Secret { get; init; }

    /// <summary>
    /// Named HTTP client to use for delivery.
    /// </summary>
    public required string HttpClientName { get; init; }

    /// <summary>
    /// Maximum delivery attempts.
    /// </summary>
    public required int MaxAttempts { get; init; }

    /// <summary>
    /// Base retry delay in milliseconds.
    /// </summary>
    public required int InitialBackoffMs { get; init; }

    /// <summary>
    /// Maximum retry delay in milliseconds.
    /// </summary>
    public required int MaxBackoffMs { get; init; }

    /// <summary>
    /// Per-request timeout in seconds.
    /// </summary>
    public required int RequestTimeoutSeconds { get; init; }
}
