// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Events;

/// <summary>
/// Stored feature-change event envelope used for replay and webhook delivery.
/// </summary>
internal sealed record FeatureChangeEvent
{
    public required string EventId { get; init; }
    public required long Cursor { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string ServiceId { get; init; }
    public required int LayerId { get; init; }
    public required long ObjectId { get; init; }
    public required string Operation { get; init; }
    public required string Protocol { get; init; }
    public required string RequestId { get; init; }
}

/// <summary>
/// Input payload used by writers to publish feature-change events.
/// </summary>
internal sealed record FeatureChangeEventRequest
{
    public required string ServiceId { get; init; }
    public required int LayerId { get; init; }
    public required long ObjectId { get; init; }
    public required string Operation { get; init; }
    public required string Protocol { get; init; }
    public required string RequestId { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
}

/// <summary>
/// Feature-change event configuration.
/// </summary>
public sealed class FeatureChangeEventOptions
{
    public const string SectionName = "FeatureChangeEvents";

    /// <summary>
    /// Maximum number of events retained in-memory for replay.
    /// </summary>
    public int MaxRetainedEvents { get; set; } = 20_000;
}

/// <summary>
/// Webhook delivery configuration for feature-change events.
/// </summary>
public sealed class FeatureChangeWebhookOptions
{
    public const string SectionName = "FeatureChangeEvents:Webhook";

    /// <summary>
    /// Enables outbound webhook delivery.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Absolute webhook URL.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Shared HMAC secret for signature generation.
    /// </summary>
    public string? Secret { get; set; }

    /// <summary>
    /// Maximum delivery attempts per event.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// Base retry delay in milliseconds. Exponential backoff doubles this value per attempt.
    /// </summary>
    public int InitialBackoffMs { get; set; } = 500;

    /// <summary>
    /// Upper bound for retry delay in milliseconds.
    /// </summary>
    public int MaxBackoffMs { get; set; } = 30_000;

    /// <summary>
    /// Per-request timeout for webhook calls in seconds.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 15;
}

