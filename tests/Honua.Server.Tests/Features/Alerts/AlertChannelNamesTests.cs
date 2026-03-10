// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;
using Xunit;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class AlertChannelNamesTests
{
    [Theory]
    [InlineData("webhook", AlertChannelType.Webhook)]
    [InlineData("websocket", AlertChannelType.WebSocket)]
    [InlineData("email", AlertChannelType.Email)]
    [InlineData("digest", AlertChannelType.Digest)]
    [InlineData("aws_sns", AlertChannelType.AwsSns)]
    [InlineData("microsoft_teams", AlertChannelType.MicrosoftTeams)]
    [InlineData("azure_eventhub", AlertChannelType.AzureEventHub)]
    [Trait("Category", "Unit")]
    public void TryParse_WithCanonicalNames_ParsesExpectedChannel(string value, AlertChannelType expected)
    {
        var parsed = AlertChannelNames.TryParse(value, out var channelType);

        Assert.True(parsed);
        Assert.Equal(expected, channelType);
    }

    [Theory]
    [InlineData(AlertChannelType.Webhook, "webhook")]
    [InlineData(AlertChannelType.WebSocket, "websocket")]
    [InlineData(AlertChannelType.Email, "email")]
    [InlineData(AlertChannelType.Digest, "digest")]
    [InlineData(AlertChannelType.AwsSns, "aws_sns")]
    [InlineData(AlertChannelType.AzureEventGrid, "azure_eventgrid")]
    [InlineData(AlertChannelType.Slack, "slack")]
    [InlineData(AlertChannelType.MicrosoftTeams, "microsoft_teams")]
    [InlineData(AlertChannelType.AwsSqs, "aws_sqs")]
    [InlineData(AlertChannelType.AzureEventHub, "azure_eventhub")]
    [Trait("Category", "Unit")]
    public void ToExternalName_ReturnsCanonicalSnakeCase(AlertChannelType channelType, string expected)
    {
        Assert.Equal(expected, channelType.ToExternalName());
    }

    [Theory]
    [InlineData("awssns", AlertChannelType.AwsSns)]
    [InlineData("azureeventgrid", AlertChannelType.AzureEventGrid)]
    [InlineData("teams", AlertChannelType.MicrosoftTeams)]
    [InlineData("microsoftteams", AlertChannelType.MicrosoftTeams)]
    [InlineData("awssqs", AlertChannelType.AwsSqs)]
    [InlineData("azureeventhub", AlertChannelType.AzureEventHub)]
    [Trait("Category", "Unit")]
    public void TryParse_WithLegacyNames_RemainsCompatible(string value, AlertChannelType expected)
    {
        var parsed = AlertChannelNames.TryParse(value, out var channelType);

        Assert.True(parsed);
        Assert.Equal(expected, channelType);
    }
}
