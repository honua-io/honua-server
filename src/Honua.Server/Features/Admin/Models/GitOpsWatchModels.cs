// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Configuration options for the GitOps repository watch feature.
/// </summary>
public sealed class GitOpsWatchOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "GitOpsWatch";

    /// <summary>
    /// Whether the GitOps watch feature is enabled (enterprise edition).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Minimum allowed poll interval in seconds (floor for user-configured values).
    /// </summary>
    public int MinPollIntervalSeconds { get; set; } = 30;
}

/// <summary>
/// Request to configure a git repository for watching.
/// </summary>
public sealed class GitOpsWatchConfigRequest
{
    /// <summary>
    /// Git repository URL (HTTPS or SSH).
    /// </summary>
    [JsonPropertyName("repositoryUrl")]
    public string RepositoryUrl { get; init; } = string.Empty;

    /// <summary>
    /// Branch to watch for changes.
    /// </summary>
    [JsonPropertyName("branch")]
    public string Branch { get; init; } = "main";

    /// <summary>
    /// Relative exact manifest file path or directory path within the repository. Directory paths resolve to
    /// honua-manifest.json first, then manifest.json; slashless paths without a file extension are normalized as
    /// directories. Glob patterns are not supported.
    /// </summary>
    [JsonPropertyName("manifestPath")]
    public string ManifestPath { get; init; } = "manifests/";

    /// <summary>
    /// Poll interval in seconds for change detection.
    /// </summary>
    [JsonPropertyName("pollIntervalSeconds")]
    public int PollIntervalSeconds { get; init; } = 60;

    /// <summary>
    /// Whether approval is required before applying detected changes.
    /// </summary>
    [JsonPropertyName("approvalRequired")]
    public bool ApprovalRequired { get; init; }

    /// <summary>
    /// Whether to delete server resources absent from the repository manifest.
    /// Defaults to false for safety; enable for full GitOps reconciliation.
    /// </summary>
    [JsonPropertyName("pruneEnabled")]
    public bool PruneEnabled { get; init; }

    /// <summary>
    /// Whether the watch should be active immediately.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Identity of the actor configuring the watch.
    /// </summary>
    [JsonPropertyName("configuredBy")]
    public string? ConfiguredBy { get; init; }
}

/// <summary>
/// Response payload for a GitOps watch configuration.
/// </summary>
public sealed class GitOpsWatchConfigResponse
{
    /// <summary>
    /// Unique identifier for the watch configuration.
    /// </summary>
    [JsonPropertyName("configId")]
    public Guid ConfigId { get; init; }

    /// <summary>
    /// Git repository URL.
    /// </summary>
    [JsonPropertyName("repositoryUrl")]
    public string RepositoryUrl { get; init; } = string.Empty;

    /// <summary>
    /// Branch being watched.
    /// </summary>
    [JsonPropertyName("branch")]
    public string Branch { get; init; } = string.Empty;

    /// <summary>
    /// Relative exact manifest file path or directory path.
    /// </summary>
    [JsonPropertyName("manifestPath")]
    public string ManifestPath { get; init; } = string.Empty;

    /// <summary>
    /// Poll interval in seconds.
    /// </summary>
    [JsonPropertyName("pollIntervalSeconds")]
    public int PollIntervalSeconds { get; init; }

    /// <summary>
    /// Whether approval is required for changes.
    /// </summary>
    [JsonPropertyName("approvalRequired")]
    public bool ApprovalRequired { get; init; }

    /// <summary>
    /// Whether to delete server resources absent from the repository manifest.
    /// </summary>
    [JsonPropertyName("pruneEnabled")]
    public bool PruneEnabled { get; init; }

    /// <summary>
    /// Whether the watch is currently active.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    /// <summary>
    /// Last known commit SHA from the watched branch.
    /// </summary>
    [JsonPropertyName("lastKnownCommitSha")]
    public string? LastKnownCommitSha { get; init; }

    /// <summary>
    /// Timestamp of the last successful poll.
    /// </summary>
    [JsonPropertyName("lastPolledAt")]
    public DateTimeOffset? LastPolledAt { get; init; }

    /// <summary>
    /// Identity of the actor who configured the watch.
    /// </summary>
    [JsonPropertyName("configuredBy")]
    public string? ConfiguredBy { get; init; }

    /// <summary>
    /// Timestamp when the configuration was created.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Timestamp when the configuration was last updated.
    /// </summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Response payload for a GitOps change record.
/// </summary>
public sealed class GitOpsChangeRecordResponse
{
    /// <summary>
    /// Unique identifier for the change record.
    /// </summary>
    [JsonPropertyName("changeId")]
    public Guid ChangeId { get; init; }

    /// <summary>
    /// Reference to the watch configuration.
    /// </summary>
    [JsonPropertyName("configId")]
    public Guid ConfigId { get; init; }

    /// <summary>
    /// Git commit SHA that triggered the change.
    /// </summary>
    [JsonPropertyName("commitSha")]
    public string CommitSha { get; init; } = string.Empty;

    /// <summary>
    /// Git commit message.
    /// </summary>
    [JsonPropertyName("commitMessage")]
    public string? CommitMessage { get; init; }

    /// <summary>
    /// Git commit author.
    /// </summary>
    [JsonPropertyName("commitAuthor")]
    public string? CommitAuthor { get; init; }

    /// <summary>
    /// Timestamp of the git commit.
    /// </summary>
    [JsonPropertyName("commitTimestamp")]
    public DateTimeOffset? CommitTimestamp { get; init; }

    /// <summary>
    /// Outcome of the change application.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// Optional reference to the pending approval record.
    /// </summary>
    [JsonPropertyName("pendingApprovalId")]
    public Guid? PendingApprovalId { get; init; }

    /// <summary>
    /// Summary of what was applied.
    /// </summary>
    [JsonPropertyName("applySummary")]
    public string? ApplySummary { get; init; }

    /// <summary>
    /// Error message if the apply failed.
    /// </summary>
    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Timestamp when the change was detected.
    /// </summary>
    [JsonPropertyName("detectedAt")]
    public DateTimeOffset DetectedAt { get; init; }

    /// <summary>
    /// Timestamp when the change was applied.
    /// </summary>
    [JsonPropertyName("appliedAt")]
    public DateTimeOffset? AppliedAt { get; init; }
}

/// <summary>
/// Response payload for a GitOps change diff (before/after manifest).
/// </summary>
public sealed class GitOpsChangeDiffResponse
{
    /// <summary>
    /// Unique identifier for the change record.
    /// </summary>
    [JsonPropertyName("changeId")]
    public Guid ChangeId { get; init; }

    /// <summary>
    /// Git commit SHA that triggered the change.
    /// </summary>
    [JsonPropertyName("commitSha")]
    public string CommitSha { get; init; } = string.Empty;

    /// <summary>
    /// Manifest state before the change (null for first apply).
    /// </summary>
    [JsonPropertyName("before")]
    public JsonElement? Before { get; init; }

    /// <summary>
    /// Manifest state after the change.
    /// </summary>
    [JsonPropertyName("after")]
    public JsonElement After { get; init; }
}
