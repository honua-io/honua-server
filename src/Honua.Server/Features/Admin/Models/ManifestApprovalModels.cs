// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Response payload for a pending manifest change.
/// </summary>
public sealed class ManifestPendingChangeResponse
{
    /// <summary>
    /// Unique identifier for the pending change.
    /// </summary>
    [JsonPropertyName("pendingId")]
    public Guid PendingId { get; init; }

    /// <summary>
    /// Hash of the manifest content.
    /// </summary>
    [JsonPropertyName("manifestHash")]
    public string ManifestHash { get; init; } = string.Empty;

    /// <summary>
    /// Current status of the pending change.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// Identity of the actor who requested the change.
    /// </summary>
    [JsonPropertyName("requestedBy")]
    public string? RequestedBy { get; init; }

    /// <summary>
    /// Reason for the change request.
    /// </summary>
    [JsonPropertyName("requestedReason")]
    public string? RequestedReason { get; init; }

    /// <summary>
    /// Identity of the actor who made the decision.
    /// </summary>
    [JsonPropertyName("decisionBy")]
    public string? DecisionBy { get; init; }

    /// <summary>
    /// Reason for the decision.
    /// </summary>
    [JsonPropertyName("decisionReason")]
    public string? DecisionReason { get; init; }

    /// <summary>
    /// Number of resources in the manifest snapshot.
    /// </summary>
    [JsonPropertyName("resourceCount")]
    public int ResourceCount { get; init; }

    /// <summary>
    /// Whether the original apply was a dry-run.
    /// </summary>
    [JsonPropertyName("dryRun")]
    public bool DryRun { get; init; }

    /// <summary>
    /// Whether the original apply had pruning enabled.
    /// </summary>
    [JsonPropertyName("prune")]
    public bool Prune { get; init; }

    /// <summary>
    /// Timestamp when the change was submitted.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Timestamp when a decision was made.
    /// </summary>
    [JsonPropertyName("decidedAt")]
    public DateTimeOffset? DecidedAt { get; init; }

    /// <summary>
    /// Timestamp after which the pending change expires.
    /// </summary>
    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>
/// Request payload for approving a pending manifest change.
/// </summary>
public sealed class ManifestApproveRequest
{
    /// <summary>
    /// Identity of the approver.
    /// </summary>
    [JsonPropertyName("approvedBy")]
    public string? ApprovedBy { get; init; }

    /// <summary>
    /// Optional reason for approval.
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>
/// Request payload for rejecting a pending manifest change.
/// </summary>
public sealed class ManifestRejectRequest
{
    /// <summary>
    /// Identity of the rejector.
    /// </summary>
    [JsonPropertyName("rejectedBy")]
    public string? RejectedBy { get; init; }

    /// <summary>
    /// Reason for rejection.
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>
/// Configuration options for manifest approval workflows.
/// </summary>
public sealed class ManifestApprovalOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "ManifestApproval";

    /// <summary>
    /// Whether the approval workflow feature is enabled (enterprise edition).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Default timeout in minutes for pending approvals. Null means no expiry.
    /// </summary>
    public int? DefaultTimeoutMinutes { get; set; } = 1440;

    /// <summary>
    /// Interval in seconds for the expiry background service to scan.
    /// </summary>
    public int ExpiryScanIntervalSeconds { get; set; } = 60;
}

/// <summary>
/// Configuration options for manifest approval webhooks.
/// </summary>
public sealed class ManifestApprovalWebhookOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "ManifestApproval:Webhook";

    /// <summary>
    /// Enables outbound webhook delivery for approval events.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Absolute webhook URL (HTTPS required).
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
    /// Base retry delay in milliseconds.
    /// </summary>
    public int InitialBackoffMs { get; set; } = 500;

    /// <summary>
    /// Upper bound for retry delay in milliseconds.
    /// </summary>
    public int MaxBackoffMs { get; set; } = 30_000;

    /// <summary>
    /// Per-request timeout in seconds.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 15;
}

/// <summary>
/// Webhook event payload for manifest approval lifecycle events.
/// </summary>
public sealed class ManifestApprovalWebhookEvent
{
    /// <summary>
    /// Unique event identifier.
    /// </summary>
    [JsonPropertyName("eventId")]
    public string EventId { get; init; } = string.Empty;

    /// <summary>
    /// Event type (manifest-approval-requested, manifest-approved, manifest-rejected).
    /// </summary>
    [JsonPropertyName("eventType")]
    public string EventType { get; init; } = string.Empty;

    /// <summary>
    /// Pending change identifier.
    /// </summary>
    [JsonPropertyName("pendingId")]
    public Guid PendingId { get; init; }

    /// <summary>
    /// Hash of the manifest.
    /// </summary>
    [JsonPropertyName("manifestHash")]
    public string ManifestHash { get; init; } = string.Empty;

    /// <summary>
    /// Current status.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// Actor who triggered the event.
    /// </summary>
    [JsonPropertyName("actor")]
    public string? Actor { get; init; }

    /// <summary>
    /// Reason associated with the event.
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>
    /// Number of resources in the manifest.
    /// </summary>
    [JsonPropertyName("resourceCount")]
    public int ResourceCount { get; init; }

    /// <summary>
    /// Timestamp of the event.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; }
}
