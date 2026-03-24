// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Metadata.Domain;

/// <summary>
/// Represents a queued manifest change awaiting approval.
/// </summary>
public sealed class ManifestPendingChange
{
    /// <summary>
    /// Unique identifier for the pending change.
    /// </summary>
    public Guid PendingId { get; init; }

    /// <summary>
    /// Serialized manifest snapshot containing the resources to apply.
    /// </summary>
    public JsonElement ManifestSnapshot { get; init; }

    /// <summary>
    /// Hash of the manifest content for deduplication and integrity.
    /// </summary>
    public string ManifestHash { get; init; } = string.Empty;

    /// <summary>
    /// Current status of the pending change.
    /// </summary>
    public ManifestApprovalStatus Status { get; init; }

    /// <summary>
    /// Identity of the actor who requested the change.
    /// </summary>
    public string? RequestedBy { get; init; }

    /// <summary>
    /// Free-form reason for the change request.
    /// </summary>
    public string? RequestedReason { get; init; }

    /// <summary>
    /// Identity of the actor who approved or rejected the change.
    /// </summary>
    public string? DecisionBy { get; init; }

    /// <summary>
    /// Reason provided when the change was approved or rejected.
    /// </summary>
    public string? DecisionReason { get; init; }

    /// <summary>
    /// Whether the original apply request used dry-run mode.
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Whether the original apply request had pruning enabled.
    /// </summary>
    public bool Prune { get; init; }

    /// <summary>
    /// Number of resources in the manifest snapshot.
    /// </summary>
    public int ResourceCount { get; init; }

    /// <summary>
    /// Timestamp when the change was submitted.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Timestamp when the change was approved or rejected.
    /// </summary>
    public DateTimeOffset? DecidedAt { get; init; }

    /// <summary>
    /// Timestamp after which the pending change expires and is auto-rejected.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>
/// Approval lifecycle statuses for manifest pending changes.
/// </summary>
public enum ManifestApprovalStatus
{
    /// <summary>
    /// Change is awaiting review.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Change has been reserved for application but not finalized yet.
    /// Internal transition state used to prevent concurrent decisions.
    /// </summary>
    Applying = 4,

    /// <summary>
    /// Change was approved and applied.
    /// </summary>
    Approved = 1,

    /// <summary>
    /// Change was rejected by a reviewer.
    /// </summary>
    Rejected = 2,

    /// <summary>
    /// Change expired before a decision was made.
    /// </summary>
    Expired = 3
}
