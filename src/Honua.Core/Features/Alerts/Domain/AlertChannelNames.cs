// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Alerts.Domain;

/// <summary>
/// Converts alert channel types to and from their canonical external names.
/// </summary>
public static class AlertChannelNames
{
    /// <summary>
    /// Returns the canonical snake_case name for a channel type.
    /// </summary>
    /// <param name="channelType">Channel type to convert.</param>
    /// <returns>Canonical external channel name.</returns>
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

    /// <summary>
    /// Parses a channel name from admin or persisted payloads.
    /// </summary>
    /// <param name="value">Channel name to parse.</param>
    /// <returns>The parsed channel type.</returns>
    public static AlertChannelType Parse(string value)
    {
        if (TryParse(value, out var channelType))
        {
            return channelType;
        }

        throw new InvalidOperationException($"Unsupported alert channel '{value}'.");
    }

    /// <summary>
    /// Tries to parse a channel name from admin or persisted payloads.
    /// </summary>
    /// <param name="value">Channel name to parse.</param>
    /// <param name="channelType">Parsed channel type when successful.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
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
