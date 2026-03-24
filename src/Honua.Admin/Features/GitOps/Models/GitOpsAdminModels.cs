// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Admin.Features.GitOps.Models;

internal sealed class ApiEnvelope<T>
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("data")]
    public T? Data { get; init; }
}

internal sealed class GitOpsWatchConfigSaveRequest
{
    [JsonPropertyName("repositoryUrl")]
    public string RepositoryUrl { get; set; } = string.Empty;

    [JsonPropertyName("branch")]
    public string Branch { get; set; } = "main";

    [JsonPropertyName("manifestPath")]
    public string ManifestPath { get; set; } = "manifests/";

    [JsonPropertyName("pollIntervalSeconds")]
    public int PollIntervalSeconds { get; set; } = 60;

    [JsonPropertyName("approvalRequired")]
    public bool ApprovalRequired { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("configuredBy")]
    public string? ConfiguredBy { get; set; }
}

internal sealed class GitOpsWatchConfigModel
{
    [JsonPropertyName("configId")]
    public Guid ConfigId { get; init; }

    [JsonPropertyName("repositoryUrl")]
    public string RepositoryUrl { get; init; } = string.Empty;

    [JsonPropertyName("branch")]
    public string Branch { get; init; } = string.Empty;

    [JsonPropertyName("manifestPath")]
    public string ManifestPath { get; init; } = string.Empty;

    [JsonPropertyName("pollIntervalSeconds")]
    public int PollIntervalSeconds { get; init; }

    [JsonPropertyName("approvalRequired")]
    public bool ApprovalRequired { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("lastKnownCommitSha")]
    public string? LastKnownCommitSha { get; init; }

    [JsonPropertyName("lastPolledAt")]
    public DateTimeOffset? LastPolledAt { get; init; }

    [JsonPropertyName("configuredBy")]
    public string? ConfiguredBy { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class GitOpsChangeRecordModel
{
    [JsonPropertyName("changeId")]
    public Guid ChangeId { get; init; }

    [JsonPropertyName("configId")]
    public Guid ConfigId { get; init; }

    [JsonPropertyName("commitSha")]
    public string CommitSha { get; init; } = string.Empty;

    [JsonPropertyName("commitMessage")]
    public string? CommitMessage { get; init; }

    [JsonPropertyName("commitAuthor")]
    public string? CommitAuthor { get; init; }

    [JsonPropertyName("commitTimestamp")]
    public DateTimeOffset? CommitTimestamp { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("pendingApprovalId")]
    public Guid? PendingApprovalId { get; init; }

    [JsonPropertyName("applySummary")]
    public string? ApplySummary { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("detectedAt")]
    public DateTimeOffset DetectedAt { get; init; }

    [JsonPropertyName("appliedAt")]
    public DateTimeOffset? AppliedAt { get; init; }
}

internal sealed class GitOpsChangeDiffModel
{
    [JsonPropertyName("changeId")]
    public Guid ChangeId { get; init; }

    [JsonPropertyName("commitSha")]
    public string CommitSha { get; init; } = string.Empty;

    [JsonPropertyName("before")]
    public JsonElement? Before { get; init; }

    [JsonPropertyName("after")]
    public JsonElement After { get; init; }
}

internal sealed class ManifestPendingChangeModel
{
    [JsonPropertyName("pendingId")]
    public Guid PendingId { get; init; }

    [JsonPropertyName("manifestHash")]
    public string ManifestHash { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("requestedBy")]
    public string? RequestedBy { get; init; }

    [JsonPropertyName("requestedReason")]
    public string? RequestedReason { get; init; }

    [JsonPropertyName("decisionBy")]
    public string? DecisionBy { get; init; }

    [JsonPropertyName("decisionReason")]
    public string? DecisionReason { get; init; }

    [JsonPropertyName("resourceCount")]
    public int ResourceCount { get; init; }

    [JsonPropertyName("dryRun")]
    public bool DryRun { get; init; }

    [JsonPropertyName("prune")]
    public bool Prune { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("decidedAt")]
    public DateTimeOffset? DecidedAt { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }
}

internal sealed class ManifestApproveRequestModel
{
    [JsonPropertyName("approvedBy")]
    public string? ApprovedBy { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

internal sealed class ManifestRejectRequestModel
{
    [JsonPropertyName("rejectedBy")]
    public string? RejectedBy { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}
