// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Validation;
using Microsoft.Extensions.Options;

namespace Honua.Alerts;

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
            var payload = JsonSerializer.Serialize(
                new TeamsAlertPayload
                {
                    Type = "MessageCard",
                    Context = "https://schema.org/extensions",
                    ThemeColor = themeColor,
                    Summary = $"Honua Alert: {alertEvent.TriggerType} ({alertEvent.Severity})",
                    Sections =
                    [
                        new TeamsAlertSection
                        {
                            ActivityTitle = $"Honua Alert: {alertEvent.TriggerType}",
                            ActivitySubtitle = $"Incident {alertEvent.IncidentStatus}",
                            Facts =
                            [
                                new TeamsAlertFact { Name = "Severity", Value = alertEvent.Severity.ToString() },
                                new TeamsAlertFact { Name = "Status", Value = alertEvent.IncidentStatus.ToString() },
                                new TeamsAlertFact { Name = "Rule ID", Value = alertEvent.RuleId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                                new TeamsAlertFact { Name = "Layer", Value = alertEvent.LayerId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                                new TeamsAlertFact { Name = "Feature", Value = alertEvent.ObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                                new TeamsAlertFact { Name = "Occurred At", Value = alertEvent.OccurredAt.ToString("O") }
                            ],
                            Markdown = true
                        }
                    ]
                },
                AlertDeliveryJsonContext.Default.TeamsAlertPayload);

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
        catch (Exception)
        {
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = true,
                Error = "Teams delivery failed."
            };
        }
    }
}
