// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.FieldWorkflows.Review;

public static class FieldReviewStatus
{
    public const string Pending = "pending";

    public const string InReview = "in_review";

    public const string ChangesRequested = "changes_requested";

    public const string Approved = "approved";

    public const string Rejected = "rejected";

    public static readonly string[] All =
    [
        Pending,
        InReview,
        ChangesRequested,
        Approved,
        Rejected
    ];

    public static bool IsValid(string? status)
        => status is not null && Array.Exists(All, s => string.Equals(s, status, StringComparison.Ordinal));
}

public static class FieldSyncStatus
{
    public const string Synced = "synced";

    public const string Rejected = "rejected";

    public const string Failed = "failed";

    public const string Pending = "pending";
}

public sealed class FieldSubmissionRecord
{
    [JsonPropertyName("submissionId")]
    public Guid SubmissionId { get; init; }

    [JsonPropertyName("formId")]
    public string FormId { get; init; } = string.Empty;

    [JsonPropertyName("formVersion")]
    public int FormVersion { get; init; }

    [JsonPropertyName("serviceId")]
    public string ServiceId { get; init; } = string.Empty;

    [JsonPropertyName("layerId")]
    public int LayerId { get; init; }

    [JsonPropertyName("targetFeatureId")]
    public long? TargetFeatureId { get; init; }

    [JsonPropertyName("operation")]
    public string Operation { get; init; } = string.Empty;

    [JsonPropertyName("syncStatus")]
    public string SyncStatus { get; init; } = FieldSyncStatus.Pending;

    [JsonPropertyName("hasValidationIssues")]
    public bool HasValidationIssues { get; init; }

    [JsonPropertyName("validation")]
    public JsonElement? Validation { get; init; }

    [JsonPropertyName("geometry")]
    public JsonElement? Geometry { get; init; }

    // The submitter id is a hash, never a raw credential.
    [JsonPropertyName("submitterHash")]
    public string? SubmitterHash { get; init; }

    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; init; }

    [JsonPropertyName("attachmentCount")]
    public int AttachmentCount { get; init; }

    [JsonPropertyName("hasConflict")]
    public bool HasConflict { get; init; }

    [JsonPropertyName("submittedAt")]
    public DateTimeOffset SubmittedAt { get; init; }

    [JsonPropertyName("review")]
    public FieldReviewState Review { get; init; } = new();
}

public sealed class FieldReviewState
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = FieldReviewStatus.Pending;

    [JsonPropertyName("assignedTo")]
    public string? AssignedTo { get; init; }

    [JsonPropertyName("decidedBy")]
    public string? DecidedBy { get; init; }

    [JsonPropertyName("decidedAt")]
    public DateTimeOffset? DecidedAt { get; init; }

    [JsonPropertyName("decisionNote")]
    public string? DecisionNote { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("etag")]
    public string ETag { get; init; } = string.Empty;
}

public sealed class FieldReviewComment
{
    [JsonPropertyName("commentId")]
    public Guid CommentId { get; init; }

    [JsonPropertyName("submissionId")]
    public Guid SubmissionId { get; init; }

    [JsonPropertyName("author")]
    public string Author { get; init; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; init; } = string.Empty;

    [JsonPropertyName("correctionRequest")]
    public bool CorrectionRequest { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class FieldReviewQuery
{
    public string? FormId { get; init; }

    public string? ServiceId { get; init; }

    public int? LayerId { get; init; }

    public string? ReviewStatus { get; init; }

    public string? AssignedTo { get; init; }

    public string? SyncStatus { get; init; }

    public bool? HasConflict { get; init; }

    public DateTimeOffset? SubmittedFrom { get; init; }

    public DateTimeOffset? SubmittedTo { get; init; }

    public int Limit { get; init; } = 50;

    public int Offset { get; init; }
}

public sealed class FieldSubmissionListResult
{
    [JsonPropertyName("items")]
    public FieldSubmissionRecord[] Items { get; init; } = [];

    [JsonPropertyName("total")]
    public long Total { get; init; }

    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    [JsonPropertyName("offset")]
    public int Offset { get; init; }
}

public sealed class FieldReviewAssignmentRequest
{
    [JsonPropertyName("assignedTo")]
    public string? AssignedTo { get; init; }
}

public sealed class FieldReviewDecisionRequest
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

public sealed class FieldReviewCommentRequest
{
    [JsonPropertyName("body")]
    public string Body { get; init; } = string.Empty;

    [JsonPropertyName("correctionRequest")]
    public bool CorrectionRequest { get; init; }
}

public sealed class FieldSubmissionDetail
{
    [JsonPropertyName("record")]
    public FieldSubmissionRecord Record { get; init; } = new();

    [JsonPropertyName("comments")]
    public FieldReviewComment[] Comments { get; init; } = [];

    [JsonPropertyName("attachments")]
    public FieldSubmissionAttachmentInfo[] Attachments { get; init; } = [];
}

public sealed class FieldSubmissionAttachmentInfo
{
    [JsonPropertyName("attachmentId")]
    public long? AttachmentId { get; init; }

    [JsonPropertyName("fieldId")]
    public string? FieldId { get; init; }

    [JsonPropertyName("filename")]
    public string? Filename { get; init; }

    [JsonPropertyName("contentType")]
    public string? ContentType { get; init; }

    [JsonPropertyName("sizeBytes")]
    public long? SizeBytes { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;
}
