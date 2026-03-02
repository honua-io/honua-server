// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Events;

/// <summary>
/// Background dispatcher that delivers feature-change events to configured webhook subscribers.
/// </summary>
internal sealed partial class FeatureChangeWebhookDispatcher(
    IFeatureChangeEventQueue queue,
    IHttpClientFactory httpClientFactory,
    IOptions<FeatureChangeWebhookOptions> options,
    ILogger<FeatureChangeWebhookDispatcher> logger) : BackgroundService
{
    private readonly IFeatureChangeEventQueue _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly FeatureChangeWebhookOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<FeatureChangeWebhookDispatcher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var featureEvent in _queue.ReadAllAsync(stoppingToken))
        {
            await DeliverWithRetryAsync(featureEvent, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task DeliverWithRetryAsync(FeatureChangeEvent featureEvent, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.Url))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.Secret))
        {
            LogWebhookConfigurationInvalid(_logger);
            return;
        }

        var payload = JsonSerializer.Serialize(featureEvent, FeatureChangeEventsJsonContext.Default.FeatureChangeEvent);
        var timestamp = featureEvent.Timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = ComputeSignature(_options.Secret, timestamp, payload);
        var maxAttempts = Math.Max(1, _options.MaxAttempts);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.RequestTimeoutSeconds)));

                using var request = new HttpRequestMessage(HttpMethod.Post, _options.Url)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                request.Headers.TryAddWithoutValidation("X-Honua-Event-Id", featureEvent.EventId);
                request.Headers.TryAddWithoutValidation("X-Honua-Event-Timestamp", timestamp);
                request.Headers.TryAddWithoutValidation("X-Honua-Signature", $"sha256={signature}");
                request.Headers.TryAddWithoutValidation("Idempotency-Key", featureEvent.EventId);

                var client = _httpClientFactory.CreateClient("feature-change-webhook");
                using var response = await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    LogDeliverySucceeded(_logger, featureEvent.EventId, response.StatusCode);
                    return;
                }

                var isRetryable = (int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests;
                LogDeliveryFailed(_logger, featureEvent.EventId, attempt, (int)response.StatusCode, isRetryable);
                if (!isRetryable || attempt == maxAttempts)
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogDeliveryException(_logger, featureEvent.EventId, attempt, ex);
                if (attempt == maxAttempts)
                {
                    return;
                }
            }

            var delayMs = Math.Min(
                Math.Max(1, _options.InitialBackoffMs) * (1 << (attempt - 1)),
                Math.Max(1, _options.MaxBackoffMs));
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ComputeSignature(string secret, string timestamp, string payload)
    {
        var message = $"{timestamp}.{payload}";
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var messageBytes = Encoding.UTF8.GetBytes(message);
        using var hmac = new HMACSHA256(secretBytes);
        var hash = hmac.ComputeHash(messageBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    [LoggerMessage(EventId = 9101, Level = LogLevel.Warning, Message = "Feature change webhook is enabled but secret is missing; delivery is disabled.")]
    private static partial void LogWebhookConfigurationInvalid(ILogger logger);

    [LoggerMessage(EventId = 9102, Level = LogLevel.Debug, Message = "Feature-change webhook delivery succeeded for event {EventId} with status {StatusCode}.")]
    private static partial void LogDeliverySucceeded(ILogger logger, string eventId, System.Net.HttpStatusCode statusCode);

    [LoggerMessage(EventId = 9103, Level = LogLevel.Warning, Message = "Feature-change webhook delivery failed for event {EventId} on attempt {Attempt} with status {StatusCode}. Retryable={Retryable}.")]
    private static partial void LogDeliveryFailed(ILogger logger, string eventId, int attempt, int statusCode, bool retryable);

    [LoggerMessage(EventId = 9104, Level = LogLevel.Warning, Message = "Feature-change webhook delivery threw for event {EventId} on attempt {Attempt}.")]
    private static partial void LogDeliveryException(ILogger logger, string eventId, int attempt, Exception exception);
}

