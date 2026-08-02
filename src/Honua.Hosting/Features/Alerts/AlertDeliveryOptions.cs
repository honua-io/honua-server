// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Features.Alerts.Domain;

namespace Honua.Alerts;

internal sealed class AlertDeliveryOptions
{
    public const string SectionName = AlertOptions.SectionName;

    public AlertDeliveryDispatchOptions Dispatch { get; set; } = new();
}

internal sealed class AlertDeliveryDispatchOptions
{
    public AwsSnsChannelOptions? AwsSns { get; set; }

    public AzureEventGridChannelOptions? AzureEventGrid { get; set; }

    public EmailChannelOptions? Email { get; set; }

    public SlackChannelOptions? Slack { get; set; }

    public TeamsChannelOptions? Teams { get; set; }

    public AwsSqsChannelOptions? AwsSqs { get; set; }

    public AzureEventHubChannelOptions? AzureEventHub { get; set; }
}

internal sealed class AwsSnsChannelOptions
{
    [Required]
    [MinLength(1)]
    public string TopicArn { get; set; } = string.Empty;

    public string? Region { get; set; }
}

internal sealed class AzureEventGridChannelOptions
{
    [Required]
    [MinLength(1)]
    public string TopicEndpoint { get; set; } = string.Empty;

    public string? TopicKey { get; set; }
}

internal sealed class EmailChannelOptions
{
    [Required]
    [MinLength(1)]
    public string SmtpHost { get; set; } = "localhost";

    [Range(1, 65535)]
    public int SmtpPort { get; set; } = 587;

    [Required]
    [MinLength(1)]
    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = "Honua Alerts";

    public string? DefaultRecipient { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    public bool UseSsl { get; set; } = true;
}

internal sealed class SlackChannelOptions
{
    [Required]
    [MinLength(1)]
    public string WebhookUrl { get; set; } = string.Empty;
}

internal sealed class TeamsChannelOptions
{
    [Required]
    [MinLength(1)]
    public string WebhookUrl { get; set; } = string.Empty;
}

internal sealed class AwsSqsChannelOptions
{
    [Required]
    [MinLength(1)]
    public string QueueUrl { get; set; } = string.Empty;

    public string? Region { get; set; }
}

internal sealed class AzureEventHubChannelOptions
{
    [Required]
    [MinLength(1)]
    public string ConnectionString { get; set; } = string.Empty;

    [Required]
    [MinLength(1)]
    public string EventHubName { get; set; } = string.Empty;
}
