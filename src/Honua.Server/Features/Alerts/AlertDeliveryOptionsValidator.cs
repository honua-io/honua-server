// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;

namespace Honua.Server.Features.Alerts;

internal sealed class AlertDeliveryOptionsValidator : OptionsValidator<AlertDeliveryOptions>
{
    protected override void ValidateOptions(AlertDeliveryOptions options, List<string> failures)
    {
        if (options.Dispatch.AwsSns is { } awsSns)
        {
            ValidateDataAnnotations(awsSns, failures, $"{nameof(AlertDeliveryOptions.Dispatch)}.{nameof(AlertDeliveryDispatchOptions.AwsSns)}");
        }

        if (options.Dispatch.AzureEventGrid is { } eventGrid)
        {
            ValidateDataAnnotations(eventGrid, failures, $"{nameof(AlertDeliveryOptions.Dispatch)}.{nameof(AlertDeliveryDispatchOptions.AzureEventGrid)}");

            if (!string.IsNullOrWhiteSpace(eventGrid.TopicEndpoint) &&
                !Uri.TryCreate(eventGrid.TopicEndpoint, UriKind.Absolute, out _))
            {
                failures.Add(
                    $"{nameof(AlertDeliveryOptions.Dispatch)}.{nameof(AlertDeliveryDispatchOptions.AzureEventGrid)}.{nameof(AzureEventGridChannelOptions.TopicEndpoint)} must be a valid absolute URL.");
            }
        }

        if (options.Dispatch.Email is { } email)
        {
            ValidateDataAnnotations(email, failures, $"{nameof(AlertDeliveryOptions.Dispatch)}.{nameof(AlertDeliveryDispatchOptions.Email)}");
        }

        if (options.Dispatch.Slack is { } slack)
        {
            ValidateDataAnnotations(slack, failures, $"{nameof(AlertDeliveryOptions.Dispatch)}.{nameof(AlertDeliveryDispatchOptions.Slack)}");

            if (!string.IsNullOrWhiteSpace(slack.WebhookUrl))
            {
                ValidateOutboundHttpUrl(
                    slack.WebhookUrl,
                    $"{nameof(AlertDeliveryOptions.Dispatch)}.{nameof(AlertDeliveryDispatchOptions.Slack)}.{nameof(SlackChannelOptions.WebhookUrl)}",
                    failures);
            }
        }

        if (options.Dispatch.Teams is { } teams)
        {
            ValidateDataAnnotations(teams, failures, $"{nameof(AlertDeliveryOptions.Dispatch)}.{nameof(AlertDeliveryDispatchOptions.Teams)}");

            if (!string.IsNullOrWhiteSpace(teams.WebhookUrl))
            {
                ValidateOutboundHttpUrl(
                    teams.WebhookUrl,
                    $"{nameof(AlertDeliveryOptions.Dispatch)}.{nameof(AlertDeliveryDispatchOptions.Teams)}.{nameof(TeamsChannelOptions.WebhookUrl)}",
                    failures);
            }
        }

        if (options.Dispatch.AwsSqs is { } awsSqs)
        {
            ValidateDataAnnotations(awsSqs, failures, $"{nameof(AlertDeliveryOptions.Dispatch)}.{nameof(AlertDeliveryDispatchOptions.AwsSqs)}");
        }

        if (options.Dispatch.AzureEventHub is { } eventHub)
        {
            ValidateDataAnnotations(eventHub, failures, $"{nameof(AlertDeliveryOptions.Dispatch)}.{nameof(AlertDeliveryDispatchOptions.AzureEventHub)}");
        }
    }
}
