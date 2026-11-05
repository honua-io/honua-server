// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Alerts;

// Alerts:Edition only caps the license and never grants features the license does not include.
internal sealed class AlertEditionPolicy : IAlertEditionPolicy
{
    private readonly AlertOptions _options;
    private readonly AlertDeliveryOptions _deliveryOptions;
    private readonly ILicenseEntitlementService _entitlements;

    public AlertEditionPolicy(
        IOptions<AlertOptions> options,
        IOptions<AlertDeliveryOptions> deliveryOptions,
        ILicenseEntitlementService entitlements)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _deliveryOptions = deliveryOptions?.Value ?? throw new ArgumentNullException(nameof(deliveryOptions));
        _entitlements = entitlements ?? throw new ArgumentNullException(nameof(entitlements));
    }

    // The tier comes from the license's declared edition, not from unrelated entitlement keys.
    private AlertEdition? EffectiveEdition
    {
        get
        {
            var licenseDerived = LicenseDerivedEdition;
            if (licenseDerived is null)
            {
                return null;
            }

            return _options.Edition is { } cap && cap < licenseDerived.Value ? cap : licenseDerived;
        }
    }

    private AlertEdition? LicenseDerivedEdition => _entitlements.GetSnapshot() switch
    {
        { IsValid: true, Edition: HonuaEdition.Enterprise } => AlertEdition.Enterprise,
        { IsValid: true, Edition: HonuaEdition.Pro } => AlertEdition.Pro,
        _ => null,
    };

    private bool IsEntitled(string entitlementKey)
        => GetEntitlementDenialReason(entitlementKey) == AlertEditionDenialReason.None;

    public AlertEditionDenialReason GetEntitlementDenialReason(string entitlementKey)
    {
        if (!_entitlements.CheckEntitlement(entitlementKey).IsActive)
        {
            return AlertEditionDenialReason.MissingEntitlement;
        }

        // The license carries the key, so anything blocking it now is the operator's own cap.
        return _options.Edition is { } cap
            && AlertEntitlementMap.GetRequiredAlertEdition(entitlementKey) > cap
                ? AlertEditionDenialReason.EditionCap
                : AlertEditionDenialReason.None;
    }

    public AlertEditionDenialReason GetChannelDenialReason(AlertChannelType channelType)
    {
        var entitlementKey = AlertEntitlementMap.GetChannelEntitlementKey(channelType);
        if (entitlementKey is not null)
        {
            return GetEntitlementDenialReason(entitlementKey);
        }

        // Keyless channels (WebSocket) are gated by the effective edition, so the same two causes
        // apply: an Enterprise license capped down to Pro is a configuration denial, anything
        // else is the license not reaching the tier.
        if (EffectiveEdition == AlertEdition.Enterprise)
        {
            return AlertEditionDenialReason.None;
        }

        return LicenseDerivedEdition == AlertEdition.Enterprise
            ? AlertEditionDenialReason.EditionCap
            : AlertEditionDenialReason.MissingEntitlement;
    }

    public bool IsTriggerAllowed(AlertTriggerType triggerType)
        => IsEntitled(AlertEntitlementMap.GetTriggerEntitlementKey(triggerType));

    public bool IsRuleAllowed(AlertRuleDefinition rule)
    {
        // The evaluation engine is separately entitled (alerts.evaluation, Pro — the catalog
        // describes it as the background worker that evaluates rules). Every background
        // evaluation path funnels through this predicate, so a license that carries a trigger key
        // but not the engine key must not get the evaluator run for it.
        if (!IsEntitled(FeatureCatalog.AlertsEvaluationKey))
        {
            return false;
        }

        if (!IsTriggerAllowed(rule.TriggerType))
        {
            return false;
        }

        // A rule's self-declared tier requirement must also fit within the effective edition.
        var effective = EffectiveEdition;
        return effective is { } edition && rule.EditionRequired <= edition;
    }

    public bool IsChannelAllowed(AlertChannelType channelType)
        => GetChannelDenialReason(channelType) == AlertEditionDenialReason.None;

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
}
