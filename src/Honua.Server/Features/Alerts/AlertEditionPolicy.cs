// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Alerts;

internal sealed class AlertEditionPolicy : IAlertEditionPolicy
{
    private readonly AlertOptions _options;

    public AlertEditionPolicy(IOptions<AlertOptions> options)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
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
            AlertChannelType.Webhook => !string.IsNullOrWhiteSpace(_options.Dispatch.DefaultWebhookUrl),
            AlertChannelType.WebSocket => false,
            AlertChannelType.Email => _options.Dispatch.Email is { SmtpHost.Length: > 0, FromAddress.Length: > 0, DefaultRecipient.Length: > 0 },
            AlertChannelType.Digest => !string.IsNullOrWhiteSpace(_options.Dispatch.Digest.WebhookUrl),
            AlertChannelType.AwsSns => !string.IsNullOrWhiteSpace(_options.Dispatch.AwsSns?.TopicArn),
            AlertChannelType.AzureEventGrid => !string.IsNullOrWhiteSpace(_options.Dispatch.AzureEventGrid?.TopicEndpoint),
            AlertChannelType.Slack => !string.IsNullOrWhiteSpace(_options.Dispatch.Slack?.WebhookUrl),
            AlertChannelType.MicrosoftTeams => !string.IsNullOrWhiteSpace(_options.Dispatch.Teams?.WebhookUrl),
            AlertChannelType.AwsSqs => !string.IsNullOrWhiteSpace(_options.Dispatch.AwsSqs?.QueueUrl),
            AlertChannelType.AzureEventHub => !string.IsNullOrWhiteSpace(_options.Dispatch.AzureEventHub?.ConnectionString)
                && !string.IsNullOrWhiteSpace(_options.Dispatch.AzureEventHub?.EventHubName),
            _ => false
        };
    }
}
