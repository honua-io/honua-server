// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Alerts;

internal sealed class AlertEditionPolicy : IAlertEditionPolicy
{
    private const string EvaluationEntitlement = "alerts.evaluation";
    private const string EnterExitEntitlement = "alerts.enter-exit";
    private const string DwellEntitlement = "alerts.dwell";
    private const string ThresholdEntitlement = "alerts.threshold";
    private const string WebhookEntitlement = "channels.webhook";
    private const string EmailEntitlement = "channels.email";
    private const string SlackEntitlement = "channels.slack";
    private const string TeamsEntitlement = "channels.teams";
    private const string AwsSnsEntitlement = "channels.aws-sns";
    private const string AzureEventGridEntitlement = "channels.azure-eventgrid";
    private const string DigestEntitlement = "channels.digest";

    private readonly AlertOptions _options;
    private readonly AlertDeliveryOptions _deliveryOptions;
    private readonly ILicenseEntitlementService _licenseEntitlements;

    public AlertEditionPolicy(
        IOptions<AlertOptions> options,
        IOptions<AlertDeliveryOptions> deliveryOptions,
        ILicenseEntitlementService licenseEntitlements)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _deliveryOptions = deliveryOptions?.Value ?? throw new ArgumentNullException(nameof(deliveryOptions));
        _licenseEntitlements = licenseEntitlements ?? throw new ArgumentNullException(nameof(licenseEntitlements));
    }

    public bool IsRuleAllowed(AlertRuleDefinition rule)
    {
        if (!IsEditionAllowed(rule.EditionRequired) ||
            !HasEntitlement(EvaluationEntitlement))
        {
            return false;
        }

        var triggerEntitlement = rule.TriggerType switch
        {
            AlertTriggerType.Enter or AlertTriggerType.Exit => EnterExitEntitlement,
            AlertTriggerType.Dwell => DwellEntitlement,
            AlertTriggerType.Threshold => ThresholdEntitlement,
            _ => null,
        };

        return triggerEntitlement is not null && HasEntitlement(triggerEntitlement);
    }

    public bool IsChannelAllowed(AlertChannelType channelType)
    {
        var requiredEdition = channelType == AlertChannelType.Webhook
            ? AlertEdition.Pro
            : AlertEdition.Enterprise;
        if (!IsEditionAllowed(requiredEdition))
        {
            return false;
        }

        var channelEntitlement = channelType switch
        {
            AlertChannelType.Webhook => WebhookEntitlement,
            AlertChannelType.Email => EmailEntitlement,
            AlertChannelType.Digest => DigestEntitlement,
            AlertChannelType.AwsSns => AwsSnsEntitlement,
            AlertChannelType.AzureEventGrid => AzureEventGridEntitlement,
            AlertChannelType.Slack => SlackEntitlement,
            AlertChannelType.MicrosoftTeams => TeamsEntitlement,
            _ => null,
        };

        if (channelEntitlement is not null)
        {
            return HasEntitlement(channelEntitlement);
        }

        // WebSocket, SQS, and Event Hubs have no signed entitlement keys in the
        // catalog. Fail closed until product governance assigns explicit keys.
        return false;
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
            AlertChannelType.AzureEventHub => _deliveryOptions.Dispatch.AzureEventHub is { } azureEventHub
                && !string.IsNullOrWhiteSpace(azureEventHub.ConnectionString)
                && !string.IsNullOrWhiteSpace(azureEventHub.EventHubName),
#endif
            _ => false
        };
    }

    private bool HasEntitlement(string entitlementKey)
        => _licenseEntitlements.CheckEntitlement(entitlementKey).IsActive;

    private bool IsEditionAllowed(AlertEdition requiredEdition)
    {
        if ((int)requiredEdition > (int)_options.Edition)
        {
            return false;
        }

        return true;
    }
}
