// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Features.Alerts.Domain;

/// <summary>
/// Top-level alerting feature configuration.
/// </summary>
public sealed class AlertOptions
{
    /// <summary>
    /// Configuration section used to bind alerting options.
    /// </summary>
    public const string SectionName = "Alerts";

    /// <summary>
    /// Enables alert processing workers.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Active edition used for feature gating.
    /// </summary>
    public AlertEdition Edition { get; init; } = AlertEdition.Pro;

    /// <summary>
    /// Evaluator worker settings.
    /// </summary>
    public AlertEvaluationOptions Evaluation { get; init; } = new();

    /// <summary>
    /// Dispatcher worker settings.
    /// </summary>
    public AlertDispatchOptions Dispatch { get; init; } = new();
}

/// <summary>
/// Settings for durable alert evaluation.
/// </summary>
public sealed class AlertEvaluationOptions
{
    /// <summary>
    /// Worker name used for checkpoint persistence.
    /// </summary>
    [Required]
    [MinLength(1)]
    [MaxLength(64)]
    public string WorkerName { get; init; } = "evaluator";

    /// <summary>
    /// Maximum number of durable changes processed per batch.
    /// </summary>
    [Range(1, 5000)]
    public int ChangeBatchSize { get; init; } = 100;

    /// <summary>
    /// Interval for dwell sweep processing.
    /// </summary>
    public TimeSpan DwellSweepInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Interval to wait when no changes are available.
    /// </summary>
    public TimeSpan IdleDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Lease duration for leader-election heartbeats.
    /// </summary>
    public TimeSpan LeaderLeaseDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Leader election strategy identifier.
    /// </summary>
    [Required]
    [MinLength(1)]
    [MaxLength(32)]
    public string LeaderElectionMode { get; init; } = "postgres-advisory-lock";
}

/// <summary>
/// Settings for alert outbox dispatch processing.
/// </summary>
public sealed class AlertDispatchOptions
{
    /// <summary>
    /// Maximum number of outbox jobs claimed per dispatch cycle.
    /// </summary>
    [Range(1, 5000)]
    public int ClaimBatchSize { get; init; } = 100;

    /// <summary>
    /// Base retry delay used by exponential backoff.
    /// </summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Maximum retry delay cap for exponential backoff.
    /// </summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Optional default webhook URL used when a dispatch row does not provide a destination.
    /// </summary>
    public string? DefaultWebhookUrl { get; init; }

    /// <summary>
    /// Delay when no dispatch work is available.
    /// </summary>
    public TimeSpan IdleDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// AWS SNS delivery settings.
    /// </summary>
    public AwsSnsAlertOptions? AwsSns { get; init; }

    /// <summary>
    /// Azure Event Grid delivery settings.
    /// </summary>
    public AzureEventGridAlertOptions? AzureEventGrid { get; init; }

    /// <summary>
    /// Email (SMTP) delivery settings.
    /// </summary>
    public EmailAlertOptions? Email { get; init; }

    /// <summary>
    /// Digest delivery settings.
    /// </summary>
    public DigestAlertOptions Digest { get; init; } = new();

    /// <summary>
    /// Slack incoming webhook settings.
    /// </summary>
    public SlackAlertOptions? Slack { get; init; }

    /// <summary>
    /// Microsoft Teams incoming webhook settings.
    /// </summary>
    public TeamsAlertOptions? Teams { get; init; }

    /// <summary>
    /// AWS SQS delivery settings.
    /// </summary>
    public AwsSqsAlertOptions? AwsSqs { get; init; }

    /// <summary>
    /// Azure Event Hub delivery settings.
    /// </summary>
    public AzureEventHubAlertOptions? AzureEventHub { get; init; }
}

/// <summary>
/// Settings for AWS SNS alert delivery.
/// </summary>
public sealed class AwsSnsAlertOptions
{
    /// <summary>
    /// SNS topic ARN to publish alert events to.
    /// </summary>
    [Required]
    [MinLength(1)]
    public string TopicArn { get; init; } = string.Empty;

    /// <summary>
    /// AWS region for the SNS topic.
    /// </summary>
    public string? Region { get; init; }
}

/// <summary>
/// Settings for Azure Event Grid alert delivery.
/// </summary>
public sealed class AzureEventGridAlertOptions
{
    /// <summary>
    /// Event Grid topic endpoint URL.
    /// </summary>
    [Required]
    [MinLength(1)]
    public string TopicEndpoint { get; init; } = string.Empty;

    /// <summary>
    /// Access key for the Event Grid topic. If null, DefaultAzureCredential is used.
    /// </summary>
    public string? TopicKey { get; init; }
}

/// <summary>
/// Settings for SMTP email alert delivery.
/// </summary>
public sealed class EmailAlertOptions
{
    /// <summary>
    /// SMTP server hostname.
    /// </summary>
    [Required]
    [MinLength(1)]
    public string SmtpHost { get; init; } = "localhost";

    /// <summary>
    /// SMTP server port.
    /// </summary>
    [Range(1, 65535)]
    public int SmtpPort { get; init; } = 587;

    /// <summary>
    /// Sender email address.
    /// </summary>
    [Required]
    [MinLength(1)]
    public string FromAddress { get; init; } = string.Empty;

    /// <summary>
    /// Sender display name.
    /// </summary>
    public string FromName { get; init; } = "Honua Alerts";

    /// <summary>
    /// Default recipient email address used when a dispatch item has no destination.
    /// </summary>
    public string? DefaultRecipient { get; init; }

    /// <summary>
    /// SMTP username for authentication.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// SMTP password for authentication.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Enables TLS/SSL for the SMTP connection.
    /// </summary>
    public bool UseSsl { get; init; } = true;
}

/// <summary>
/// Settings for batched digest alert delivery.
/// </summary>
public sealed class DigestAlertOptions
{
    /// <summary>
    /// Webhook URL for digest delivery. If null, digest items are dead-lettered.
    /// </summary>
    public string? WebhookUrl { get; init; }

    /// <summary>
    /// Interval between digest flushes.
    /// </summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Maximum events per digest batch.
    /// </summary>
    [Range(1, 10000)]
    public int MaxBatchSize { get; init; } = 50;
}

/// <summary>
/// Settings for Slack incoming webhook alert delivery.
/// </summary>
public sealed class SlackAlertOptions
{
    /// <summary>
    /// Slack incoming webhook URL.
    /// </summary>
    [Required]
    [MinLength(1)]
    public string WebhookUrl { get; init; } = string.Empty;
}

/// <summary>
/// Settings for Microsoft Teams incoming webhook alert delivery.
/// </summary>
public sealed class TeamsAlertOptions
{
    /// <summary>
    /// Teams incoming webhook URL.
    /// </summary>
    [Required]
    [MinLength(1)]
    public string WebhookUrl { get; init; } = string.Empty;
}

/// <summary>
/// Settings for AWS SQS alert delivery.
/// </summary>
public sealed class AwsSqsAlertOptions
{
    /// <summary>
    /// SQS queue URL to send alert messages to.
    /// </summary>
    [Required]
    [MinLength(1)]
    public string QueueUrl { get; init; } = string.Empty;

    /// <summary>
    /// AWS region for the SQS queue.
    /// </summary>
    public string? Region { get; init; }
}

/// <summary>
/// Settings for Azure Event Hub alert delivery.
/// </summary>
public sealed class AzureEventHubAlertOptions
{
    /// <summary>
    /// Event Hub connection string.
    /// </summary>
    [Required]
    [MinLength(1)]
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>
    /// Event Hub name.
    /// </summary>
    [Required]
    [MinLength(1)]
    public string EventHubName { get; init; } = string.Empty;
}
