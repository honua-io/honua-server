// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Alerts;

internal sealed class AlertEditionPolicy : IAlertEditionPolicy
{
    private readonly AlertOptions _options;
    private readonly AlertDeliveryOptions _deliveryOptions;

    public AlertEditionPolicy(
        IOptions<AlertOptions> options,
        IOptions<AlertDeliveryOptions> deliveryOptions)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _deliveryOptions = deliveryOptions?.Value ?? throw new ArgumentNullException(nameof(deliveryOptions));
    }

    public bool IsRuleAllowed(AlertRuleDefinition rule)
    {
        if ((int)rule.EditionRequired > (int)_options.Edition)
        {
            return false;
        }

        return _options.Edition switch
        {
            AlertEdition.Pro => rule.TriggerType is AlertTriggerType.Enter or AlertTriggerType.Exit,
            AlertEdition.Enterprise => true,
            _ => false
        };
    }

    public bool IsChannelAllowed(AlertChannelType channelType)
    {
        return _options.Edition switch
        {
            AlertEdition.Pro => channelType == AlertChannelType.Webhook,
            AlertEdition.Enterprise => channelType is AlertChannelType.Webhook
                or AlertChannelType.WebSocket
                or AlertChannelType.Email
                or AlertChannelType.Digest
                or AlertChannelType.AwsSns
                or AlertChannelType.AzureEventGrid
                or AlertChannelType.Slack
                or AlertChannelType.MicrosoftTeams
                or AlertChannelType.AwsSqs
                or AlertChannelType.AzureEventHub,
            _ => false
        };
    }

    public bool IsChannelConfigured(AlertChannelType channelType)
    {
        return channelType switch
        {
            AlertChannelType.Webhook => !string.IsNullOrWhiteSpace(_options.Dispatch.DefaultWebhookUrl)
                && !string.IsNullOrWhiteSpace(_options.Dispatch.DefaultWebhookSecret),
            AlertChannelType.WebSocket => true,
            AlertChannelType.Email => _deliveryOptions.Dispatch.Email is { SmtpHost.Length: > 0, FromAddress.Length: > 0, DefaultRecipient.Length: > 0 },
            AlertChannelType.Digest => !string.IsNullOrWhiteSpace(_options.Dispatch.Digest.WebhookUrl)
                && !string.IsNullOrWhiteSpace(_options.Dispatch.Digest.WebhookSecret),
            AlertChannelType.Slack => !string.IsNullOrWhiteSpace(_deliveryOptions.Dispatch.Slack?.WebhookUrl),
            AlertChannelType.MicrosoftTeams => !string.IsNullOrWhiteSpace(_deliveryOptions.Dispatch.Teams?.WebhookUrl),

            // AWS / Azure channels are only deliverable when the build includes their cloud SDK. In
            // no-cloud / slim builds they are backed solely by UnsupportedAlertDeliverySink, so they
            // must report as unconfigured regardless of any leftover AlertDelivery:Dispatch:* settings —
            // otherwise AlertPipeline would route to (and AlertAdminEndpoints would advertise) a channel
            // whose every delivery fails immediately with the unsupported sink.
#if HONUA_EXCLUDE_AWS
            AlertChannelType.AwsSns => false,
            AlertChannelType.AwsSqs => false,
#else
            AlertChannelType.AwsSns => !string.IsNullOrWhiteSpace(_deliveryOptions.Dispatch.AwsSns?.TopicArn),
            AlertChannelType.AwsSqs => !string.IsNullOrWhiteSpace(_deliveryOptions.Dispatch.AwsSqs?.QueueUrl),
#endif
#if HONUA_EXCLUDE_AZURE
            AlertChannelType.AzureEventGrid => false,
            AlertChannelType.AzureEventHub => false,
#else
            AlertChannelType.AzureEventGrid => !string.IsNullOrWhiteSpace(_deliveryOptions.Dispatch.AzureEventGrid?.TopicEndpoint),
            AlertChannelType.AzureEventHub => !string.IsNullOrWhiteSpace(_deliveryOptions.Dispatch.AzureEventHub?.ConnectionString)
                && !string.IsNullOrWhiteSpace(_deliveryOptions.Dispatch.AzureEventHub?.EventHubName),
#endif
            _ => false
        };
    }
}
