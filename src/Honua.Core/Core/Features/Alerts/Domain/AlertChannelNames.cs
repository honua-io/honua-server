// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Alerts.Domain;

public static class AlertChannelNames
{
    public static string ToExternalName(this AlertChannelType channelType)
    {
        return channelType switch
        {
            AlertChannelType.Webhook => "webhook",
            AlertChannelType.WebSocket => "websocket",
            AlertChannelType.Email => "email",
            AlertChannelType.Digest => "digest",
            AlertChannelType.AwsSns => "aws_sns",
            AlertChannelType.AzureEventGrid => "azure_eventgrid",
            AlertChannelType.Slack => "slack",
            AlertChannelType.MicrosoftTeams => "microsoft_teams",
            AlertChannelType.AwsSqs => "aws_sqs",
            AlertChannelType.AzureEventHub => "azure_eventhub",
            _ => throw new InvalidOperationException($"Unsupported alert channel type '{channelType}'.")
        };
    }

    public static AlertChannelType Parse(string value)
    {
        if (TryParse(value, out var channelType))
        {
            return channelType;
        }

        throw new InvalidOperationException($"Unsupported alert channel '{value}'.");
    }

    public static bool TryParse(string? value, out AlertChannelType channelType)
    {
        channelType = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "webhook":
                channelType = AlertChannelType.Webhook;
                return true;
            case "websocket":
                channelType = AlertChannelType.WebSocket;
                return true;
            case "email":
                channelType = AlertChannelType.Email;
                return true;
            case "digest":
                channelType = AlertChannelType.Digest;
                return true;
            case "aws_sns":
            case "awssns":
                channelType = AlertChannelType.AwsSns;
                return true;
            case "azure_eventgrid":
            case "azureeventgrid":
                channelType = AlertChannelType.AzureEventGrid;
                return true;
            case "slack":
                channelType = AlertChannelType.Slack;
                return true;
            case "microsoft_teams":
            case "microsoftteams":
            case "teams":
                channelType = AlertChannelType.MicrosoftTeams;
                return true;
            case "aws_sqs":
            case "awssqs":
                channelType = AlertChannelType.AwsSqs;
                return true;
            case "azure_eventhub":
            case "azureeventhub":
                channelType = AlertChannelType.AzureEventHub;
                return true;
            default:
                return false;
        }
    }
}
