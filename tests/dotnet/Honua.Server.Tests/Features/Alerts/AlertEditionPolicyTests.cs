// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Alerts;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class AlertEditionPolicyTests
{
    [UnitTest]
    public void IsChannelAllowed_CommunityLicense_DeniesConfiguredEnterpriseChannels()
    {
        var policy = CreatePolicy(HonuaEdition.Community, AlertEdition.Enterprise);

        Assert.False(policy.IsChannelAllowed(AlertChannelType.Webhook));
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
    public void IsChannelAllowed_ProLicense_AllowsWebhookOnly()
    {
        var policy = CreatePolicy(HonuaEdition.Pro, AlertEdition.Enterprise);

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
    public void IsChannelAllowed_EnterpriseLicense_AllowsCataloguedChannelsOnly()
    {
        var policy = CreatePolicy(HonuaEdition.Enterprise, AlertEdition.Enterprise);

        Assert.True(policy.IsChannelAllowed(AlertChannelType.Webhook));
        Assert.True(policy.IsChannelAllowed(AlertChannelType.Email));
        Assert.True(policy.IsChannelAllowed(AlertChannelType.Digest));
        Assert.True(policy.IsChannelAllowed(AlertChannelType.AwsSns));
        Assert.True(policy.IsChannelAllowed(AlertChannelType.AzureEventGrid));
        Assert.True(policy.IsChannelAllowed(AlertChannelType.Slack));
        Assert.True(policy.IsChannelAllowed(AlertChannelType.MicrosoftTeams));
        Assert.False(policy.IsChannelAllowed(AlertChannelType.WebSocket));
        Assert.False(policy.IsChannelAllowed(AlertChannelType.AwsSqs));
        Assert.False(policy.IsChannelAllowed(AlertChannelType.AzureEventHub));
    }

    [UnitTest]
    public void IsChannelAllowed_EnterpriseLicenseWithProCeiling_DeniesEnterpriseChannels()
    {
        var policy = CreatePolicy(HonuaEdition.Enterprise, AlertEdition.Pro);

        Assert.True(policy.IsChannelAllowed(AlertChannelType.Webhook));
        Assert.False(policy.IsChannelAllowed(AlertChannelType.Email));
        Assert.False(policy.IsChannelAllowed(AlertChannelType.Slack));
    }

    [UnitTest]
    public void IsRuleAllowed_LicenseEntitlementsControlTriggerTiers()
    {
        var proPolicy = CreatePolicy(HonuaEdition.Pro, AlertEdition.Enterprise);
        var enterprisePolicy = CreatePolicy(HonuaEdition.Enterprise, AlertEdition.Enterprise);

        Assert.True(proPolicy.IsRuleAllowed(CreateRule(AlertTriggerType.Enter, AlertEdition.Pro)));
        Assert.False(proPolicy.IsRuleAllowed(CreateRule(AlertTriggerType.Dwell, AlertEdition.Enterprise)));
        Assert.False(proPolicy.IsRuleAllowed(CreateRule(AlertTriggerType.Threshold, AlertEdition.Enterprise)));
        Assert.True(enterprisePolicy.IsRuleAllowed(CreateRule(AlertTriggerType.Dwell, AlertEdition.Enterprise)));
        Assert.True(enterprisePolicy.IsRuleAllowed(CreateRule(AlertTriggerType.Threshold, AlertEdition.Enterprise)));
    }

    [UnitTest]
    public void ExplicitEntitlements_LowerEditionLabel_AllowScopedAlertAddOn()
    {
        var entitlements = new TestLicenseEntitlementService(
            HonuaEdition.Community,
            entitlements:
            [
                "alerts.evaluation",
                "alerts.enter-exit",
                "channels.webhook"
            ]);
        var policy = new AlertEditionPolicy(
            Options.Create(new AlertOptions { Edition = AlertEdition.Enterprise }),
            Options.Create(new AlertDeliveryOptions()),
            entitlements);

        Assert.True(policy.IsRuleAllowed(CreateRule(AlertTriggerType.Enter, AlertEdition.Pro)));
        Assert.True(policy.IsChannelAllowed(AlertChannelType.Webhook));
    }

    [UnitTest]
    public void IsChannelConfigured_WithConfiguredDispatchTargets_ReturnsExpectedAvailability()
    {
        var policy = new AlertEditionPolicy(
            Options.Create(new AlertOptions
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
            }),
            Options.Create(new AlertDeliveryOptions
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
            }),
            new TestLicenseEntitlementService(HonuaEdition.Enterprise));

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

    private static AlertEditionPolicy CreatePolicy(HonuaEdition edition, AlertEdition ceiling)
        => new(
            Options.Create(new AlertOptions { Edition = ceiling }),
            Options.Create(new AlertDeliveryOptions()),
            new TestLicenseEntitlementService(edition));

    private static AlertRuleDefinition CreateRule(
        AlertTriggerType triggerType,
        AlertEdition editionRequired)
        => new()
        {
            RuleId = 1,
            ServiceId = "service",
            LayerId = 0,
            RuleName = "rule",
            TriggerType = triggerType,
            Severity = AlertSeverity.Warning,
            EditionRequired = editionRequired,
            IsActive = true,
        };
}
