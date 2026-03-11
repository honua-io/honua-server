// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Alerts;

internal sealed class TeamsAlertDeliverySink : IAlertDeliverySink
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AlertDeliveryOptions _options;

    public TeamsAlertDeliverySink(
        IHttpClientFactory httpClientFactory,
        IOptions<AlertDeliveryOptions> options)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public AlertChannelType ChannelType => AlertChannelType.MicrosoftTeams;

    public async Task<AlertDeliveryResult> DeliverAsync(
        AlertDispatchItem dispatchItem,
        AlertEventEnvelope alertEvent,
        CancellationToken cancellationToken = default)
    {
        var teamsOptions = _options.Dispatch.Teams;
        if (teamsOptions is null || string.IsNullOrWhiteSpace(teamsOptions.WebhookUrl))
        {
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = false,
                Error = "Microsoft Teams webhook URL is not configured."
            };
        }

        var webhookUrl = dispatchItem.Destination ?? teamsOptions.WebhookUrl;

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
                    Error = $"Teams webhook URL {destinationValidation.ErrorMessage ?? "must be a valid HTTPS URL."}"
                };
            }

            var themeColor = alertEvent.Severity switch
            {
                AlertSeverity.Critical => "dc3545",
                AlertSeverity.Warning => "ffc107",
                _ => "17a2b8"
            };

            // Use Office 365 Connector card format (MessageCard) for broad compatibility.
            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["@type"] = "MessageCard",
                ["@context"] = "https://schema.org/extensions",
                ["themeColor"] = themeColor,
                ["summary"] = $"Honua Alert: {alertEvent.TriggerType} ({alertEvent.Severity})",
                ["sections"] = new[]
                {
                    new
                    {
                        activityTitle = $"Honua Alert: {alertEvent.TriggerType}",
                        activitySubtitle = $"Incident {alertEvent.IncidentStatus}",
                        facts = new[]
                        {
                            new { name = "Severity", value = alertEvent.Severity.ToString() },
                            new { name = "Status", value = alertEvent.IncidentStatus.ToString() },
                            new { name = "Rule ID", value = alertEvent.RuleId.ToString() },
                            new { name = "Layer", value = alertEvent.LayerId.ToString() },
                            new { name = "Feature", value = alertEvent.ObjectId.ToString() },
                            new { name = "Occurred At", value = alertEvent.OccurredAt.ToString("O") }
                        },
                        markdown = true
                    }
                }
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, destinationValidation.Uri)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            var client = _httpClientFactory.CreateClient("alerts-teams");
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
                Error = $"Teams webhook responded with {(int)response.StatusCode}."
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
