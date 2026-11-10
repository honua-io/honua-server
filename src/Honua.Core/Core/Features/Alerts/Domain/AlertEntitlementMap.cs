// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Features.Alerts.Domain;

public static class AlertEntitlementMap
{
    public static string GetTriggerEntitlementKey(AlertTriggerType triggerType) => triggerType switch
    {
        AlertTriggerType.Enter or AlertTriggerType.Exit => FeatureCatalog.AlertsEnterExitKey,
        AlertTriggerType.Dwell => FeatureCatalog.AlertsDwellKey,
        AlertTriggerType.Threshold => FeatureCatalog.AlertsThresholdKey,
        _ => FeatureCatalog.AlertsEnterExitKey,
    };

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

    public static AlertEdition GetRequiredAlertEdition(string entitlementKey) => entitlementKey switch
    {
        FeatureCatalog.AlertsEnterExitKey
            or FeatureCatalog.AlertsEvaluationKey
            or FeatureCatalog.ChannelsWebhookKey => AlertEdition.Pro,
        _ => AlertEdition.Enterprise,
    };
}
