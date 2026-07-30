// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Alerts;

/// <summary>
/// License-derived alert edition policy (#2998). Allowed triggers and delivery channels come
/// from the active <see cref="ILicenseEntitlementService"/> entitlement keys
/// (<c>alerts.*</c>/<c>channels.*</c>, see <see cref="AlertEntitlementMap"/>). The standalone
/// <c>Alerts:Edition</c> option is a downward-only operational cap: when set, features above
/// the configured tier are blocked even if the license grants them; it never unlocks features
/// the license does not include.
/// </summary>
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

    /// <summary>
    /// The effective alert edition: the edition the active license itself declares (Enterprise,
    /// Pro, or null when the snapshot is not a valid paid license), capped by
    /// <see cref="AlertOptions.Edition"/> when configured.
    /// </summary>
    /// <remarks>
    /// The tier comes from the license's declared edition, not from probing individual
    /// entitlement keys. Using unrelated keys as tier proxies (previously
    /// <c>alerts.dwell</c> ⇒ Enterprise, <c>alerts.enter-exit</c> ⇒ Pro) misreads a signed
    /// license, because signed payloads enumerate entitlement keys independently: an Enterprise
    /// license granting <c>alerts.threshold</c> but not <c>alerts.dwell</c> resolved to Pro, so
    /// <see cref="IsRuleAllowed"/> rejected its fully entitled threshold rule. Per-key
    /// entitlement is still enforced by <see cref="IsEntitled"/>; this property only supplies the
    /// tier that the <c>Alerts:Edition</c> cap, a rule's self-declared
    /// <see cref="AlertRuleDefinition.EditionRequired"/>, and the keyless WebSocket channel
    /// compare against. An expired or otherwise invalid snapshot reports Community/not-valid and
    /// so degrades to null here.
    /// </remarks>
    private AlertEdition? EffectiveEdition
    {
        get
        {
            var snapshot = _entitlements.GetSnapshot();
            AlertEdition? licenseDerived = snapshot switch
            {
                { IsValid: true, Edition: HonuaEdition.Enterprise } => AlertEdition.Enterprise,
                { IsValid: true, Edition: HonuaEdition.Pro } => AlertEdition.Pro,
                _ => null,
            };

            if (licenseDerived is null)
            {
                return null;
            }

            return _options.Edition is { } cap && cap < licenseDerived.Value ? cap : licenseDerived;
        }
    }

    /// <summary>
    /// True when the entitlement key is active on the license AND not excluded by the
    /// downward-only <c>Alerts:Edition</c> cap.
    /// </summary>
    private bool IsEntitled(string entitlementKey)
    {
        if (!_entitlements.CheckEntitlement(entitlementKey).IsActive)
        {
            return false;
        }

        return _options.Edition is not { } cap
            || AlertEntitlementMap.GetRequiredAlertEdition(entitlementKey) <= cap;
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
    {
        var entitlementKey = AlertEntitlementMap.GetChannelEntitlementKey(channelType);
        if (entitlementKey is null)
        {
            // Channels without a catalog key of their own (WebSocket) retain their historical
            // Enterprise tier via the effective edition.
            return EffectiveEdition == AlertEdition.Enterprise;
        }

        return IsEntitled(entitlementKey);
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
}
