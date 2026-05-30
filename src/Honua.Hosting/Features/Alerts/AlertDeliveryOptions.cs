// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Features.Alerts.Domain;

namespace Honua.Alerts;

internal sealed class AlertDeliveryOptions
{
    public const string SectionName = AlertOptions.SectionName;

    public AlertDeliveryDispatchOptions Dispatch { get; init; } = new();
}

internal sealed class AlertDeliveryDispatchOptions
{
    public AwsSnsChannelOptions? AwsSns { get; init; }

    public AzureEventGridChannelOptions? AzureEventGrid { get; init; }

    public EmailChannelOptions? Email { get; init; }

    public SlackChannelOptions? Slack { get; init; }

    public TeamsChannelOptions? Teams { get; init; }

    public AwsSqsChannelOptions? AwsSqs { get; init; }

    public AzureEventHubChannelOptions? AzureEventHub { get; init; }
}

internal sealed class AwsSnsChannelOptions
{
    [Required]
    [MinLength(1)]
    public string TopicArn { get; init; } = string.Empty;

    public string? Region { get; init; }
}

internal sealed class AzureEventGridChannelOptions
{
    [Required]
    [MinLength(1)]
    public string TopicEndpoint { get; init; } = string.Empty;

    public string? TopicKey { get; init; }
}

internal sealed class EmailChannelOptions
{
    [Required]
    [MinLength(1)]
    public string SmtpHost { get; init; } = "localhost";

    [Range(1, 65535)]
    public int SmtpPort { get; init; } = 587;

    [Required]
    [MinLength(1)]
    public string FromAddress { get; init; } = string.Empty;

    public string FromName { get; init; } = "Honua Alerts";

    public string? DefaultRecipient { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }

    public bool UseSsl { get; init; } = true;
}

internal sealed class SlackChannelOptions
{
    [Required]
    [MinLength(1)]
    public string WebhookUrl { get; init; } = string.Empty;
}

internal sealed class TeamsChannelOptions
{
    [Required]
    [MinLength(1)]
    public string WebhookUrl { get; init; } = string.Empty;
}

internal sealed class AwsSqsChannelOptions
{
    [Required]
    [MinLength(1)]
    public string QueueUrl { get; init; } = string.Empty;

    public string? Region { get; init; }
}

internal sealed class AzureEventHubChannelOptions
{
    [Required]
    [MinLength(1)]
    public string ConnectionString { get; init; } = string.Empty;

    [Required]
    [MinLength(1)]
    public string EventHubName { get; init; } = string.Empty;
}
