// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;
using Honua.Alerts;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class AlertEditionPolicyTests
{
    [UnitTest]
    public void IsChannelAllowed_ProEdition_AllowsWebhookOnly()
    {
        var policy = new AlertEditionPolicy(
            Options.Create(new AlertOptions { Edition = AlertEdition.Pro }),
            Options.Create(new AlertDeliveryOptions()));

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
        var policy = new AlertEditionPolicy(
            Options.Create(new AlertOptions { Edition = AlertEdition.Enterprise }),
            Options.Create(new AlertDeliveryOptions()));

        Assert.True(policy.IsChannelAllowed(AlertChannelType.Webhook));
        Assert.True(policy.IsChannelAllowed(AlertChannelType.WebSocket));
        Assert.True(policy.IsChannelAllowed(AlertChannelType.Email));
        Assert.True(policy.IsChannelAllowed(AlertChannelType.Digest));
        Assert.True(policy.IsChannelAllowed(AlertChannelType.AwsSns));
        Assert.True(policy.IsChannelAllowed(AlertChannelType.AzureEventGrid));
        Assert.True(policy.IsChannelAllowed(AlertChannelType.Slack));
        Assert.True(policy.IsChannelAllowed(AlertChannelType.MicrosoftTeams));
        Assert.True(policy.IsChannelAllowed(AlertChannelType.AwsSqs));
        Assert.True(policy.IsChannelAllowed(AlertChannelType.AzureEventHub));
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
            }));

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
