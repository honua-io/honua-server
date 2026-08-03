// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Features.Alerts.Domain;

/// <summary>
/// Maps alert triggers and delivery channels to the <see cref="FeatureCatalog"/> entitlement
/// keys that license them (#2998). This is the single source of truth the license-derived
/// alert edition policy and the alert admin surface share, so an entitlement denial can name
/// the exact <c>alerts.*</c>/<c>channels.*</c> key that blocked a request.
/// </summary>
public static class AlertEntitlementMap
{
    /// <summary>
    /// Returns the entitlement key that licenses the given alert trigger type.
    /// Enter/Exit are the Pro-tier geofence triggers; Dwell and Threshold are Enterprise.
    /// </summary>
    /// <param name="triggerType">Trigger type to map.</param>
    /// <returns>The <see cref="FeatureCatalog"/> entitlement key for the trigger.</returns>
    public static string GetTriggerEntitlementKey(AlertTriggerType triggerType) => triggerType switch
    {
        AlertTriggerType.Enter or AlertTriggerType.Exit => FeatureCatalog.AlertsEnterExitKey,
        AlertTriggerType.Dwell => FeatureCatalog.AlertsDwellKey,
        AlertTriggerType.Threshold => FeatureCatalog.AlertsThresholdKey,
        _ => FeatureCatalog.AlertsEnterExitKey,
    };

    /// <summary>
    /// Returns the entitlement key that licenses the given delivery channel, or null for
    /// channels with no catalog key of their own (currently only WebSocket, which retains its
    /// historical Enterprise tier via the effective alert edition). AWS SQS shares the SNS key
    /// and Azure Event Hub shares the Event Grid key — same cloud family and tier.
    /// </summary>
    /// <param name="channelType">Channel type to map.</param>
    /// <returns>The entitlement key for the channel, or null when none exists.</returns>
    public static string? GetChannelEntitlementKey(AlertChannelType channelType) => channelType switch
    {
        AlertChannelType.Webhook => FeatureCatalog.ChannelsWebhookKey,
        AlertChannelType.Email => FeatureCatalog.ChannelsEmailKey,
        AlertChannelType.Slack => FeatureCatalog.ChannelsSlackKey,
        AlertChannelType.MicrosoftTeams => FeatureCatalog.ChannelsTeamsKey,
        AlertChannelType.AwsSns or AlertChannelType.AwsSqs => FeatureCatalog.ChannelsAwsSnsKey,
        AlertChannelType.AzureEventGrid or AlertChannelType.AzureEventHub => FeatureCatalog.ChannelsAzureEventGridKey,
        AlertChannelType.Digest => FeatureCatalog.ChannelsDigestKey,
        _ => null,
    };

    /// <summary>
    /// Returns the minimum <see cref="AlertEdition"/> tier an alerts/channels entitlement key
    /// belongs to, used to apply the downward-only <c>Alerts:Edition</c> configuration cap.
    /// </summary>
    /// <param name="entitlementKey">An <c>alerts.*</c> or <c>channels.*</c> entitlement key.</param>
    /// <returns>The alert edition tier the key requires.</returns>
    public static AlertEdition GetRequiredAlertEdition(string entitlementKey) => entitlementKey switch
    {
        FeatureCatalog.AlertsEnterExitKey
            or FeatureCatalog.AlertsEvaluationKey
            or FeatureCatalog.ChannelsWebhookKey => AlertEdition.Pro,
        _ => AlertEdition.Enterprise,
    };
}
