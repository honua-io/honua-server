// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using System.Globalization;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Configuration;
using Honua.Server.Features.Infrastructure.Events;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Alerts;

/// <summary>
/// Background service that batches digest dispatch rows and delivers them to the
/// configured digest webhook endpoint.
/// </summary>
internal sealed partial class DigestFlushBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AlertOptions _options;
    private readonly ILogger<DigestFlushBackgroundService> _logger;

    public DigestFlushBackgroundService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IOptions<AlertOptions> options,
        ILogger<DigestFlushBackgroundService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.Dispatch.Digest.WebhookUrl))
        {
            LogDisabled(_logger);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.Dispatch.Digest.FlushInterval, stoppingToken).ConfigureAwait(false);
                await FlushAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogLoopFailed(_logger, ex);
            }
        }
    }

    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var dispatchStore = scope.ServiceProvider.GetRequiredService<IAlertDispatchStore>();
        var eventStore = scope.ServiceProvider.GetRequiredService<IAlertEventStore>();

        var claimedItems = await dispatchStore
            .ClaimPendingDigestAsync(_options.Dispatch.Digest.MaxBatchSize, now, cancellationToken)
            .ConfigureAwait(false);

        if (claimedItems.Count == 0)
        {
            return;
        }

        var batchItems = new List<(AlertDispatchItem DispatchItem, AlertEventEnvelope Event)>(claimedItems.Count);
        foreach (var item in claimedItems)
        {
            var alertEvent = await eventStore.GetAsync(item.EventId, cancellationToken).ConfigureAwait(false);
            if (alertEvent is null)
            {
                await dispatchStore
                    .MarkFailedAsync(item.DispatchId, now, now, deadLetter: true, "Alert event not found.", cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            batchItems.Add((item, alertEvent));
        }

        if (batchItems.Count == 0)
        {
            return;
        }

        LogFlushing(_logger, batchItems.Count);

        var webhookUrl = _options.Dispatch.Digest.WebhookUrl!;
        var destinationValidation = await OutboundHttpUrlValidator
            .ValidateAsync(webhookUrl, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!destinationValidation.IsValid || destinationValidation.Uri is null)
        {
            await MarkBatchFailedAsync(
                batchItems,
                dispatchStore,
                now,
                retryable: false,
                $"Digest webhook URL validation failed: {destinationValidation.ErrorMessage}",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.Dispatch.Digest.WebhookSecret))
        {
            await MarkBatchFailedAsync(
                batchItems,
                dispatchStore,
                now,
                retryable: false,
                "Digest webhook signing secret is not configured.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var payload = JsonSerializer.Serialize(batchItems.Select(static item => new
        {
            item.Event.DedupeKey,
            item.Event.RuleId,
            item.Event.ZoneId,
            item.Event.ServiceId,
            item.Event.LayerId,
            item.Event.ObjectId,
            TriggerType = item.Event.TriggerType.ToString(),
            Severity = item.Event.Severity.ToString(),
            IncidentStatus = item.Event.IncidentStatus.ToString(),
            item.Event.IncidentDurationMs,
            item.Event.OccurredAt,
            Payload = item.Event.PayloadJson
        }));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, destinationValidation.Uri)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            var timestamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var signature = WebhookDeliveryHelper.ComputeSignature(_options.Dispatch.Digest.WebhookSecret, timestamp, payload);
            WebhookDeliveryHelper.AddValidatedHeader(request.Headers, "X-Honua-Digest-Count", batchItems.Count.ToString(CultureInfo.InvariantCulture));
            WebhookDeliveryHelper.AddValidatedHeader(request.Headers, "X-Honua-Event-Timestamp", timestamp);
            WebhookDeliveryHelper.AddValidatedHeader(request.Headers, "X-Honua-Signature", $"sha256={signature}");

            var client = _httpClientFactory.CreateClient("alerts-digest");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                foreach (var (dispatchItem, _) in batchItems)
                {
                    await dispatchStore.MarkDeliveredAsync(dispatchItem.DispatchId, now, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            var retryable = (int)response.StatusCode >= 500 || (int)response.StatusCode == 429;
            await MarkBatchFailedAsync(
                batchItems,
                dispatchStore,
                now,
                retryable,
                $"Digest webhook responded with {(int)response.StatusCode}.",
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await MarkBatchFailedAsync(batchItems, dispatchStore, now, retryable: true, ex.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MarkBatchFailedAsync(
        IReadOnlyList<(AlertDispatchItem DispatchItem, AlertEventEnvelope Event)> batchItems,
        IAlertDispatchStore dispatchStore,
        DateTimeOffset attemptedAt,
        bool retryable,
        string? error,
        CancellationToken cancellationToken)
    {
        foreach (var (dispatchItem, _) in batchItems)
        {
            var nextAttempt = AlertDispatchRetryPolicy.ComputeNextAttempt(
                dispatchItem.Attempts + 1,
                attemptedAt,
                _options.Dispatch);
            var exhausted = dispatchItem.Attempts + 1 >= dispatchItem.MaxAttempts || !retryable;

            await dispatchStore
                .MarkFailedAsync(dispatchItem.DispatchId, attemptedAt, nextAttempt, exhausted, error, cancellationToken)
                .ConfigureAwait(false);
        }

        LogBatchFailed(_logger, error ?? "Digest delivery failed.");
    }

    [LoggerMessage(EventId = 9430, Level = LogLevel.Information, Message = "Digest flush service is disabled.")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(EventId = 9431, Level = LogLevel.Debug, Message = "Flushing digest batch of {Count} events.")]
    private static partial void LogFlushing(ILogger logger, int count);

    [LoggerMessage(EventId = 9432, Level = LogLevel.Warning, Message = "Digest batch delivery failed. Error={Error}.")]
    private static partial void LogBatchFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 9433, Level = LogLevel.Warning, Message = "Digest flush loop failed.")]
    private static partial void LogLoopFailed(ILogger logger, Exception exception);
}
