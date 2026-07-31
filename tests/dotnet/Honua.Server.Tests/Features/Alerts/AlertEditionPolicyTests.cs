// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Alerts;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Alerts;

/// <summary>
/// Unit tests for the license-derived <see cref="AlertEditionPolicy"/> (#2998): allowed
/// triggers/channels come from the active license entitlements, and <c>Alerts:Edition</c> acts
/// only as a downward cap — never an upward grant.
/// </summary>
public sealed class AlertEditionPolicyTests
{
    private static AlertEditionPolicy CreatePolicy(
        HonuaEdition licenseEdition,
        AlertEdition? editionCap = null,
        AlertOptions? options = null,
        AlertDeliveryOptions? deliveryOptions = null,
        string[]? entitlements = null,
        LicenseValidationState validationState = LicenseValidationState.Valid)
        => new(
            Options.Create(options ?? new AlertOptions { Edition = editionCap }),
            Options.Create(deliveryOptions ?? new AlertDeliveryOptions()),
            new TestLicenseEntitlementService(licenseEdition, validationState, entitlements));

    private static AlertRuleDefinition CreateRule(
        AlertTriggerType triggerType,
        AlertEdition editionRequired = AlertEdition.Pro) => new()
        {
            RuleId = 1,
            ServiceId = "svc",
            LayerId = 0,
            RuleName = "rule",
            TriggerType = triggerType,
            ConditionsJson = "{}",
            CooldownSeconds = 30,
            Severity = AlertSeverity.Warning,
            EditionRequired = editionRequired,
            Channels = ImmutableArray.Create(AlertChannelType.Webhook),
            IsActive = true,
        };

    [UnitTest]
    public void IsChannelAllowed_CommunityEdition_AllowsNothing()
    {
        var policy = CreatePolicy(HonuaEdition.Community);

        foreach (var channel in Enum.GetValues<AlertChannelType>())
        {
            Assert.False(policy.IsChannelAllowed(channel));
        }
    }

    [UnitTest]
    public void IsChannelAllowed_ProEdition_AllowsWebhookOnly()
    {
        var policy = CreatePolicy(HonuaEdition.Pro);

        Assert.True(policy.IsChannelAllowed(AlertChannelType.Webhook));
        Assert.False(policy.IsChannelAllowed(AlertChannelType.WebSocket));
        Assert.False(policy.IsChannelAllowed(AlertChannelType.Email));
        Assert.False(policy.IsChannelAllowed(AlertChannelType.Digest));
        Assert.False(policy.IsChannelAllowed(AlertChannelType.AwsSns));
        Assert.False(policy.IsChannelAllowed(AlertChannelType.AzureEventGrid));
        Assert.False(policy.IsChannelAllowed(AlertChannelType.Slack));
        Assert.False(policy.IsChannelAllowed(AlertChannelType.MicrosoftTeams));
        Assert.False(policy.IsChannelAllowed(AlertChannelType.AwsSqs));
        Assert.False(policy.IsChannelAllowed(AlertChannelType.AzureEventHub));
    }

    [UnitTest]
    public void IsChannelAllowed_EnterpriseEdition_AllowsAllChannels()
    {
        var policy = CreatePolicy(HonuaEdition.Enterprise);

        foreach (var channel in Enum.GetValues<AlertChannelType>())
        {
            Assert.True(policy.IsChannelAllowed(channel));
        }
    }

    [UnitTest]
    public void IsRuleAllowed_FollowsEditionDerivedTriggerTiers()
    {
        var community = CreatePolicy(HonuaEdition.Community);
        var pro = CreatePolicy(HonuaEdition.Pro);
        var enterprise = CreatePolicy(HonuaEdition.Enterprise);

        Assert.False(community.IsRuleAllowed(CreateRule(AlertTriggerType.Enter)));

        Assert.True(pro.IsRuleAllowed(CreateRule(AlertTriggerType.Enter)));
        Assert.True(pro.IsRuleAllowed(CreateRule(AlertTriggerType.Exit)));
        Assert.False(pro.IsRuleAllowed(CreateRule(AlertTriggerType.Dwell)));
        Assert.False(pro.IsRuleAllowed(CreateRule(AlertTriggerType.Threshold)));
        Assert.False(pro.IsRuleAllowed(CreateRule(AlertTriggerType.Enter, AlertEdition.Enterprise)),
            "a rule self-declared as Enterprise must not run at an effective Pro tier");

        Assert.True(enterprise.IsRuleAllowed(CreateRule(AlertTriggerType.Dwell, AlertEdition.Enterprise)));
        Assert.True(enterprise.IsRuleAllowed(CreateRule(AlertTriggerType.Threshold, AlertEdition.Enterprise)));
    }

    [UnitTest]
    public void IsRuleAllowed_EnterpriseTierGrantingThresholdButNotDwell_AllowsThresholdRule()
    {
        // Signed payloads enumerate entitlement keys independently, so an Enterprise-tier grant
        // can include alerts.threshold and omit alerts.dwell. Inferring the tier from key presence
        // (dwell => Enterprise) resolved this to Pro/null and rejected the entitled rule.
        var policy = CreatePolicy(
            HonuaEdition.Enterprise,
            entitlements:
            [
                FeatureCatalog.AlertsEvaluationKey,
                FeatureCatalog.AlertsThresholdKey,
                FeatureCatalog.ChannelsWebhookKey,
            ]);

        Assert.True(
            policy.IsRuleAllowed(CreateRule(AlertTriggerType.Threshold, AlertEdition.Enterprise)),
            "the tier must come from the edition the license declares, not from unrelated keys");
        Assert.True(policy.IsTriggerAllowed(AlertTriggerType.Threshold));

        // The keys the license genuinely omits are still enforced per key.
        Assert.False(policy.IsRuleAllowed(CreateRule(AlertTriggerType.Dwell, AlertEdition.Enterprise)));
        Assert.False(policy.IsTriggerAllowed(AlertTriggerType.Dwell));
        Assert.False(policy.IsChannelAllowed(AlertChannelType.Email));
    }

    [UnitTest]
    public void IsRuleAllowed_WithoutEvaluationEntitlement_BlocksOtherwiseEntitledRule()
    {
        // alerts.evaluation licenses the background evaluation engine. Every evaluation path
        // funnels through IsRuleAllowed, so a grant carrying only the trigger key must not run it.
        var policy = CreatePolicy(
            HonuaEdition.Pro,
            entitlements: [FeatureCatalog.AlertsEnterExitKey, FeatureCatalog.ChannelsWebhookKey]);

        Assert.True(
            policy.IsTriggerAllowed(AlertTriggerType.Enter),
            "the trigger key itself is granted, so the denial must be attributable to the engine key");
        Assert.False(policy.IsRuleAllowed(CreateRule(AlertTriggerType.Enter)));

        // Adding the engine key is the only difference that unblocks the same rule.
        var withEngine = CreatePolicy(
            HonuaEdition.Pro,
            entitlements:
            [
                FeatureCatalog.AlertsEnterExitKey,
                FeatureCatalog.AlertsEvaluationKey,
                FeatureCatalog.ChannelsWebhookKey,
            ]);
        Assert.True(withEngine.IsRuleAllowed(CreateRule(AlertTriggerType.Enter)));
    }

    [UnitTest]
    public void IsChannelAllowed_ExpiredEnterpriseTier_AllowsNothing()
    {
        // The keyless WebSocket channel resolves through the effective edition, so an expired
        // snapshot must degrade to "no tier" rather than keeping its declared Enterprise label.
        var policy = CreatePolicy(
            HonuaEdition.Enterprise,
            validationState: LicenseValidationState.Expired,
            entitlements: []);

        Assert.False(policy.IsChannelAllowed(AlertChannelType.WebSocket));
        Assert.False(policy.IsRuleAllowed(CreateRule(AlertTriggerType.Enter)));
    }

    [UnitTest]
    public void EditionCap_RestrictsBelowGrantedTier_ButNeverGrantsAbove()
    {
        // Enterprise license capped to Pro: Enterprise features blocked, Pro features intact.
        var capped = CreatePolicy(HonuaEdition.Enterprise, editionCap: AlertEdition.Pro);
        Assert.True(capped.IsChannelAllowed(AlertChannelType.Webhook));
        Assert.False(capped.IsChannelAllowed(AlertChannelType.Email));
        Assert.False(capped.IsChannelAllowed(AlertChannelType.WebSocket));
        Assert.True(capped.IsRuleAllowed(CreateRule(AlertTriggerType.Enter)));
        Assert.False(capped.IsRuleAllowed(CreateRule(AlertTriggerType.Dwell)));

        // Community license with the knob set to Enterprise: the license wins — no grant.
        var overreaching = CreatePolicy(HonuaEdition.Community, editionCap: AlertEdition.Enterprise);
        Assert.False(overreaching.IsChannelAllowed(AlertChannelType.Webhook));
        Assert.False(overreaching.IsChannelAllowed(AlertChannelType.Email));
        Assert.False(overreaching.IsRuleAllowed(CreateRule(AlertTriggerType.Enter)));
    }

    [UnitTest]
    public void GetDenialReason_SeparatesEditionCapDenialsFromMissingEntitlements()
    {
        // An Enterprise license capped to Pro still *has* alerts.dwell/channels.email. Reporting
        // that as a missing entitlement would tell an operator to buy a license they already own,
        // so the cap must be attributable on its own.
        var capped = CreatePolicy(HonuaEdition.Enterprise, editionCap: AlertEdition.Pro);

        Assert.Equal(
            AlertEditionDenialReason.EditionCap,
            capped.GetEntitlementDenialReason(FeatureCatalog.AlertsDwellKey));
        Assert.Equal(
            AlertEditionDenialReason.EditionCap,
            capped.GetChannelDenialReason(AlertChannelType.Email));

        // The keyless WebSocket channel resolves through the effective edition and must make the
        // same distinction.
        Assert.Equal(
            AlertEditionDenialReason.EditionCap,
            capped.GetChannelDenialReason(AlertChannelType.WebSocket));

        // Pro-tier features are untouched by a Pro cap.
        Assert.Equal(
            AlertEditionDenialReason.None,
            capped.GetEntitlementDenialReason(FeatureCatalog.AlertsEnterExitKey));
        Assert.Equal(
            AlertEditionDenialReason.None,
            capped.GetChannelDenialReason(AlertChannelType.Webhook));

        // Uncapped licenses that genuinely lack the key report the licensing cause instead.
        var pro = CreatePolicy(HonuaEdition.Pro);
        Assert.Equal(
            AlertEditionDenialReason.MissingEntitlement,
            pro.GetEntitlementDenialReason(FeatureCatalog.AlertsDwellKey));
        Assert.Equal(
            AlertEditionDenialReason.MissingEntitlement,
            pro.GetChannelDenialReason(AlertChannelType.Email));
        Assert.Equal(
            AlertEditionDenialReason.MissingEntitlement,
            pro.GetChannelDenialReason(AlertChannelType.WebSocket));

        // A Community license with the knob set upward is still a licensing denial: the cap is
        // downward-only, so it is never the cause of a block.
        var overreaching = CreatePolicy(HonuaEdition.Community, editionCap: AlertEdition.Enterprise);
        Assert.Equal(
            AlertEditionDenialReason.MissingEntitlement,
            overreaching.GetEntitlementDenialReason(FeatureCatalog.AlertsEnterExitKey));
        Assert.Equal(
            AlertEditionDenialReason.MissingEntitlement,
            overreaching.GetChannelDenialReason(AlertChannelType.WebSocket));
    }

    [UnitTest]
    public void GetDenialReason_AgreesWithTheAllowPredicates()
    {
        // IsTriggerAllowed/IsChannelAllowed are defined as "no denial reason"; pin that so the two
        // surfaces cannot drift into disagreeing about the same key.
        foreach (var policy in new[]
                 {
                     CreatePolicy(HonuaEdition.Community),
                     CreatePolicy(HonuaEdition.Pro),
                     CreatePolicy(HonuaEdition.Enterprise),
                     CreatePolicy(HonuaEdition.Enterprise, editionCap: AlertEdition.Pro),
                 })
        {
            foreach (var channel in Enum.GetValues<AlertChannelType>())
            {
                Assert.Equal(
                    policy.IsChannelAllowed(channel),
                    policy.GetChannelDenialReason(channel) == AlertEditionDenialReason.None);
            }

            foreach (var trigger in Enum.GetValues<AlertTriggerType>())
            {
                var key = AlertEntitlementMap.GetTriggerEntitlementKey(trigger);
                Assert.Equal(
                    policy.IsTriggerAllowed(trigger),
                    policy.GetEntitlementDenialReason(key) == AlertEditionDenialReason.None);
            }
        }
    }

    [UnitTest]
    public void IsChannelConfigured_WithConfiguredDispatchTargets_ReturnsExpectedAvailability()
    {
        var policy = CreatePolicy(
            HonuaEdition.Enterprise,
            options: new AlertOptions
            {
                Dispatch = new AlertDispatchOptions
                {
                    DefaultWebhookUrl = "https://hooks.example.com/alerts",
                    DefaultWebhookSecret = "signing-secret",
                    Digest = new DigestAlertOptions
                    {
                        WebhookUrl = "https://hooks.example.com/digest",
                        WebhookSecret = "digest-secret"
                    }
                }
            },
            deliveryOptions: new AlertDeliveryOptions
            {
                Dispatch = new AlertDeliveryDispatchOptions
                {
                    Email = new EmailChannelOptions
                    {
                        SmtpHost = "smtp.example.com",
                        FromAddress = "alerts@example.com",
                        DefaultRecipient = "ops@example.com"
                    },
                    AwsSns = new AwsSnsChannelOptions { TopicArn = "arn:aws:sns:us-east-1:123456789012:test-topic" },
                    AzureEventGrid = new AzureEventGridChannelOptions { TopicEndpoint = "https://alerts.example.com/api/events" },
                    Slack = new SlackChannelOptions { WebhookUrl = "https://hooks.slack.com/services/T00/B00/xxx" },
                    Teams = new TeamsChannelOptions { WebhookUrl = "https://outlook.office.com/webhook/xxx" },
                    AwsSqs = new AwsSqsChannelOptions { QueueUrl = "https://sqs.us-east-1.amazonaws.com/123456/test-queue" },
                    AzureEventHub = new AzureEventHubChannelOptions
                    {
                        ConnectionString = "Endpoint=sb://alerts.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=xxx",
                        EventHubName = "alerts"
                    }
                }
            });

        Assert.True(policy.IsChannelConfigured(AlertChannelType.Webhook));
        Assert.True(policy.IsChannelConfigured(AlertChannelType.WebSocket));
        Assert.True(policy.IsChannelConfigured(AlertChannelType.Email));
        Assert.True(policy.IsChannelConfigured(AlertChannelType.Digest));
        Assert.True(policy.IsChannelConfigured(AlertChannelType.AwsSns));
        Assert.True(policy.IsChannelConfigured(AlertChannelType.AzureEventGrid));
        Assert.True(policy.IsChannelConfigured(AlertChannelType.Slack));
        Assert.True(policy.IsChannelConfigured(AlertChannelType.MicrosoftTeams));
        Assert.True(policy.IsChannelConfigured(AlertChannelType.AwsSqs));
        Assert.True(policy.IsChannelConfigured(AlertChannelType.AzureEventHub));
    }
}
