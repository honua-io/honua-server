// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Alerts.Ops;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Validation;
using Microsoft.Extensions.Options;

namespace Honua.Alerts;

internal sealed partial class SlackAlertDeliverySink : IAlertDeliverySink
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AlertDeliveryOptions _options;
    private readonly ILogger<SlackAlertDeliverySink>? _logger;

    public SlackAlertDeliverySink(
        IHttpClientFactory httpClientFactory,
        IOptions<AlertDeliveryOptions> options,
        ILogger<SlackAlertDeliverySink>? logger = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public AlertChannelType ChannelType => AlertChannelType.Slack;

    public async Task<AlertDeliveryResult> DeliverAsync(
        AlertDispatchItem dispatchItem,
        AlertEventEnvelope alertEvent,
        CancellationToken cancellationToken = default)
    {
        var slackOptions = _options.Dispatch.Slack;
        if (slackOptions is null || string.IsNullOrWhiteSpace(slackOptions.WebhookUrl))
        {
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = false,
                Error = "Slack webhook URL is not configured."
            };
        }

        var webhookUrl = dispatchItem.Destination ?? slackOptions.WebhookUrl;

        try
        {
            var destinationValidation = await OutboundHttpUrlValidator
                .ValidateAsync(webhookUrl, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!destinationValidation.IsValid || destinationValidation.Uri is null)
            {
                return new AlertDeliveryResult
                {
                    Succeeded = false,
                    Retryable = false,
                    Error = $"Slack webhook URL {destinationValidation.ErrorMessage ?? "must be a valid HTTPS URL."}"
                };
            }

            var color = alertEvent.Severity switch
            {
                AlertSeverity.Critical => "#dc3545",
                AlertSeverity.Warning => "#ffc107",
                _ => "#17a2b8"
            };

            var attachment = OpsNotificationPresentation.TryResolve(alertEvent) is { } ops
                ? BuildOpsAttachment(alertEvent, ops, color)
                : BuildGisAttachment(alertEvent, color);

            var payload = JsonSerializer.Serialize(
                new SlackAlertPayload { Attachments = [attachment] },
                AlertDeliveryJsonContext.Default.SlackAlertPayload);

            using var request = new HttpRequestMessage(HttpMethod.Post, destinationValidation.Uri)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            var client = _httpClientFactory.CreateClient("alerts-slack");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return new AlertDeliveryResult { Succeeded = true, Retryable = false };
            }

            var retryable = (int)response.StatusCode >= 500 || (int)response.StatusCode == 429;
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = retryable,
                Error = $"Slack webhook responded with {(int)response.StatusCode}."
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Intentional catch-all: this is a best-effort per-alert delivery attempt inside the
        // alert dispatch pipeline; any unexpected failure (network, serialization, etc.) must
        // be reported as a retryable delivery failure rather than throwing out of the sink.
        catch (Exception ex)
        {
            if (_logger is not null)
            {
                LogDeliveryFailed(_logger, alertEvent.DedupeKey, ex);
            }
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = true,
                Error = "Slack delivery failed."
            };
        }
    }

    private static SlackAlertAttachment BuildGisAttachment(AlertEventEnvelope alertEvent, string color)
    {
        var statusEmoji = alertEvent.IncidentStatus switch
        {
            AlertIncidentStatus.Started => ":red_circle:",
            AlertIncidentStatus.Ongoing => ":large_orange_circle:",
            AlertIncidentStatus.Ended => ":white_check_mark:",
            _ => ":bell:"
        };

        return new SlackAlertAttachment
        {
            Color = color,
            Fallback = $"Honua Alert: {alertEvent.TriggerType} ({alertEvent.Severity})",
            Title = $"{statusEmoji} Honua Alert: {alertEvent.TriggerType}",
            Text = $"*Severity:* {alertEvent.Severity}\n*Status:* {alertEvent.IncidentStatus}\n*Rule:* {alertEvent.RuleId}\n*Layer:* {alertEvent.LayerId} / Feature: {alertEvent.ObjectId}",
            Footer = "Honua Alerts",
            Timestamp = alertEvent.OccurredAt.ToUnixTimeSeconds()
        };
    }

    private static SlackAlertAttachment BuildOpsAttachment(
        AlertEventEnvelope alertEvent,
        OpsAlertPayload ops,
        string color)
    {
        var text = new StringBuilder();
        text.Append("*Severity:* ").Append(alertEvent.Severity).Append("\n*Source:* ").Append(ops.Source);
        if (!string.IsNullOrWhiteSpace(ops.Body))
        {
            text.Append('\n').Append(ops.Body);
        }

        var attributes = OpsNotificationPresentation.FormatAttributes(ops.Attributes, "\n");
        if (attributes.Length > 0)
        {
            text.Append('\n').Append(attributes);
        }

        return new SlackAlertAttachment
        {
            Color = color,
            Fallback = $"Honua Ops: {ops.Title} ({alertEvent.Severity})",
            Title = $"{OpsNotificationPresentation.SeverityEmoji(alertEvent.Severity)} {ops.Title}",
            Text = text.ToString(),
            Footer = "Honua Ops",
            Timestamp = alertEvent.OccurredAt.ToUnixTimeSeconds()
        };
    }

    [LoggerMessage(EventId = 9442, Level = LogLevel.Warning, Message = "Slack alert delivery failed for event {DedupeKey}.")]
    private static partial void LogDeliveryFailed(ILogger logger, string dedupeKey, Exception exception);
}
