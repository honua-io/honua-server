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
        AlertDeliveryOptions? deliveryOptions = null)
        => new(
            Options.Create(options ?? new AlertOptions { Edition = editionCap }),
            Options.Create(deliveryOptions ?? new AlertDeliveryOptions()),
            new TestLicenseEntitlementService(licenseEdition));

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
