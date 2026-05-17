// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Metadata.Domain;

/// <summary>
/// Represents a configured git repository being watched for manifest changes.
/// </summary>
public sealed class GitOpsWatchConfig
{
    /// <summary>
    /// Unique identifier for the watch configuration.
    /// </summary>
    public Guid ConfigId { get; init; }

    /// <summary>
    /// Git repository URL (HTTPS or SSH).
    /// </summary>
    public string RepositoryUrl { get; init; } = string.Empty;

    /// <summary>
    /// Branch to watch for changes.
    /// </summary>
    public string Branch { get; init; } = "main";

    /// <summary>
    /// Relative exact manifest file path or directory path within the repository. Directory paths resolve to
    /// honua-manifest.json first, then manifest.json; slashless paths without a file extension are normalized as
    /// directories. Glob patterns are not supported.
    /// </summary>
    public string ManifestPath { get; init; } = "manifests/";

    /// <summary>
    /// Poll interval in seconds for change detection.
    /// </summary>
    public int PollIntervalSeconds { get; init; } = 60;

    /// <summary>
    /// Whether approval is required before applying detected changes.
    /// </summary>
    public bool ApprovalRequired { get; init; }

    /// <summary>
    /// Whether to delete server resources that are absent from the repository manifest.
    /// Defaults to false for safety; enable for full GitOps reconciliation.
    /// </summary>
    public bool PruneEnabled { get; init; }

    /// <summary>
    /// Whether the watch is currently active.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Last known commit SHA from the watched branch.
    /// </summary>
    public string? LastKnownCommitSha { get; init; }

    /// <summary>
    /// Timestamp of the last successful poll.
    /// </summary>
    public DateTimeOffset? LastPolledAt { get; init; }

    /// <summary>
    /// Timestamp when the configuration was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Timestamp when the configuration was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Identity of the actor who created or last updated the configuration.
    /// </summary>
    public string? ConfiguredBy { get; init; }
}

/// <summary>
/// Represents a detected change from the watched git repository.
/// </summary>
public sealed class GitOpsChangeRecord
{
    /// <summary>
    /// Unique identifier for the change record.
    /// </summary>
    public Guid ChangeId { get; init; }

    /// <summary>
    /// Reference to the watch configuration that produced this change.
    /// </summary>
    public Guid ConfigId { get; init; }

    /// <summary>
    /// Git commit SHA that triggered the change.
    /// </summary>
    public string CommitSha { get; init; } = string.Empty;

    /// <summary>
    /// Git commit message.
    /// </summary>
    public string? CommitMessage { get; init; }

    /// <summary>
    /// Git commit author.
    /// </summary>
    public string? CommitAuthor { get; init; }

    /// <summary>
    /// Timestamp of the git commit.
    /// </summary>
    public DateTimeOffset? CommitTimestamp { get; init; }

    /// <summary>
    /// Manifest snapshot before the change was applied (for diff).
    /// </summary>
    public JsonElement? ManifestBefore { get; init; }

    /// <summary>
    /// Manifest snapshot after the change (the new manifest content).
    /// </summary>
    public JsonElement ManifestAfter { get; init; }

    /// <summary>
    /// Outcome of the change application.
    /// </summary>
    public GitOpsChangeStatus Status { get; init; }

    /// <summary>
    /// Optional reference to the pending approval record, if approval was required.
    /// </summary>
    public Guid? PendingApprovalId { get; init; }

    /// <summary>
    /// Summary of what was applied (e.g., "Created: 2, Updated: 1").
    /// </summary>
    public string? ApplySummary { get; init; }

    /// <summary>
    /// Error message if the apply failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Timestamp when the change was detected.
    /// </summary>
    public DateTimeOffset DetectedAt { get; init; }

    /// <summary>
    /// Timestamp when the change was applied (or attempted).
    /// </summary>
    public DateTimeOffset? AppliedAt { get; init; }
}

/// <summary>
/// Status of a detected GitOps change.
/// </summary>
public enum GitOpsChangeStatus
{
    /// <summary>
    /// Change was detected and automatically applied.
    /// </summary>
    Applied = 0,

    /// <summary>
    /// Change was detected and queued for approval.
    /// </summary>
    PendingApproval = 1,

    /// <summary>
    /// Change application failed.
    /// </summary>
    Failed = 2,

    /// <summary>
    /// Change was skipped (e.g., no manifest differences).
    /// </summary>
    Skipped = 3
}

/// <summary>
/// Wire-format helpers for <see cref="GitOpsChangeStatus"/>.
/// </summary>
public static class GitOpsChangeStatusExtensions
{
    /// <summary>
    /// Converts the status to its canonical wire-format string (used in API responses and database storage).
    /// </summary>
    public static string ToWireString(this GitOpsChangeStatus status) => status switch
    {
        GitOpsChangeStatus.Applied => "applied",
        GitOpsChangeStatus.PendingApproval => "pending_approval",
        GitOpsChangeStatus.Failed => "failed",
        GitOpsChangeStatus.Skipped => "skipped",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown GitOps change status.")
    };

    /// <summary>
    /// Parses a wire-format string back to the enum value.
    /// </summary>
    public static GitOpsChangeStatus ParseWireString(string status) => status switch
    {
        "applied" => GitOpsChangeStatus.Applied,
        "pending_approval" => GitOpsChangeStatus.PendingApproval,
        "failed" => GitOpsChangeStatus.Failed,
        "skipped" => GitOpsChangeStatus.Skipped,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown GitOps change status string.")
    };
}
