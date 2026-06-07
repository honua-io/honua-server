// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.FieldWorkflows.Review;

/// <summary>
/// Stable review lifecycle states for a mobile-submitted field record.
/// </summary>
public static class FieldReviewStatus
{
    /// <summary>Submitted and awaiting reviewer triage.</summary>
    public const string Pending = "pending";

    /// <summary>Assigned to a reviewer and under active review.</summary>
    public const string InReview = "in_review";

    /// <summary>Reviewer requested corrections from the field collector.</summary>
    public const string ChangesRequested = "changes_requested";

    /// <summary>Record approved by a reviewer.</summary>
    public const string Approved = "approved";

    /// <summary>Record rejected by a reviewer.</summary>
    public const string Rejected = "rejected";

    /// <summary>Enumerates the well-known review statuses.</summary>
    public static readonly string[] All =
    [
        Pending,
        InReview,
        ChangesRequested,
        Approved,
        Rejected
    ];

    /// <summary>Whether the supplied value is a recognized review status.</summary>
    public static bool IsValid(string? status)
        => status is not null && Array.Exists(All, s => string.Equals(s, status, StringComparison.Ordinal));
}

/// <summary>
/// Sync classification derived from the underlying submission lifecycle, exposed
/// to reviewers so they can distinguish accepted, rejected, and failed syncs
/// without reading provider-internal submission status values.
/// </summary>
public static class FieldSyncStatus
{
    /// <summary>The submission was accepted and applied to the target layer.</summary>
    public const string Synced = "synced";

    /// <summary>The submission was rejected by validation or policy.</summary>
    public const string Rejected = "rejected";

    /// <summary>The submission failed to apply due to a server-side error.</summary>
    public const string Failed = "failed";

    /// <summary>The submission is still in flight.</summary>
    public const string Pending = "pending";
}

/// <summary>
/// Read-only projection of a mobile-submitted field record for back-office
/// review. Combines durable submission data (geometry, attachments, validation
/// state, submitter/device metadata, sync status) with the server-owned review
/// state. This is the canonical contract consumed by the console and by mobile
/// SDK clients; mobile must not define a local equivalent.
/// </summary>
public sealed class FieldSubmissionRecord
{
    /// <summary>Server submission identifier.</summary>
    [JsonPropertyName("submissionId")]
    public Guid SubmissionId { get; init; }

    /// <summary>Form package id this record was submitted against.</summary>
    [JsonPropertyName("formId")]
    public string FormId { get; init; } = string.Empty;

    /// <summary>Form package version this record was submitted against.</summary>
    [JsonPropertyName("formVersion")]
    public int FormVersion { get; init; }

    /// <summary>Target feature service id.</summary>
    [JsonPropertyName("serviceId")]
    public string ServiceId { get; init; } = string.Empty;

    /// <summary>Target feature layer id.</summary>
    [JsonPropertyName("layerId")]
    public int LayerId { get; init; }

    /// <summary>Target feature id when the submission was applied.</summary>
    [JsonPropertyName("targetFeatureId")]
    public long? TargetFeatureId { get; init; }

    /// <summary>Submitted edit operation (create, update, delete).</summary>
    [JsonPropertyName("operation")]
    public string Operation { get; init; } = string.Empty;

    /// <summary>Sync classification, one of <see cref="FieldSyncStatus"/>.</summary>
    [JsonPropertyName("syncStatus")]
    public string SyncStatus { get; init; } = FieldSyncStatus.Pending;

    /// <summary>Whether the submission carried validation issues.</summary>
    [JsonPropertyName("hasValidationIssues")]
    public bool HasValidationIssues { get; init; }

    /// <summary>Sanitized validation payload captured at submission time, when present.</summary>
    [JsonPropertyName("validation")]
    public JsonElement? Validation { get; init; }

    /// <summary>Pre-sanitized submitted geometry, when the submission carried one.</summary>
    [JsonPropertyName("geometry")]
    public JsonElement? Geometry { get; init; }

    /// <summary>Hashed submitter/actor identifier (never a raw credential).</summary>
    [JsonPropertyName("submitterHash")]
    public string? SubmitterHash { get; init; }

    /// <summary>Client/device identifier supplied with the submission, when captured.</summary>
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; init; }

    /// <summary>Count of accepted attachments persisted for this submission.</summary>
    [JsonPropertyName("attachmentCount")]
    public int AttachmentCount { get; init; }

    /// <summary>
    /// Whether this submission is flagged as conflicting (for example a sync
    /// conflict reported during offline reconciliation).
    /// </summary>
    [JsonPropertyName("hasConflict")]
    public bool HasConflict { get; init; }

    /// <summary>UTC submission timestamp.</summary>
    [JsonPropertyName("submittedAt")]
    public DateTimeOffset SubmittedAt { get; init; }

    /// <summary>Server-owned review state for this record.</summary>
    [JsonPropertyName("review")]
    public FieldReviewState Review { get; init; } = new();
}

/// <summary>
/// Server-owned review state for a submitted field record. Drives review queues,
/// assignment, and approval/rejection. All transitions are audited.
/// </summary>
public sealed class FieldReviewState
{
    /// <summary>Current review status, one of <see cref="FieldReviewStatus"/>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = FieldReviewStatus.Pending;

    /// <summary>Reviewer the record is assigned to, when assigned.</summary>
    [JsonPropertyName("assignedTo")]
    public string? AssignedTo { get; init; }

    /// <summary>Actor that recorded the most recent review decision.</summary>
    [JsonPropertyName("decidedBy")]
    public string? DecidedBy { get; init; }

    /// <summary>UTC timestamp of the most recent review decision.</summary>
    [JsonPropertyName("decidedAt")]
    public DateTimeOffset? DecidedAt { get; init; }

    /// <summary>Optional reviewer note attached to the most recent decision.</summary>
    [JsonPropertyName("decisionNote")]
    public string? DecisionNote { get; init; }

    /// <summary>UTC timestamp the review state was last updated.</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Optimistic-concurrency token for the review state.</summary>
    [JsonPropertyName("etag")]
    public string ETag { get; init; } = string.Empty;
}

/// <summary>
/// A single reviewer comment or correction request attached to a submission.
/// </summary>
public sealed class FieldReviewComment
{
    /// <summary>Stable comment identifier.</summary>
    [JsonPropertyName("commentId")]
    public Guid CommentId { get; init; }

    /// <summary>Submission this comment belongs to.</summary>
    [JsonPropertyName("submissionId")]
    public Guid SubmissionId { get; init; }

    /// <summary>Actor that authored the comment.</summary>
    [JsonPropertyName("author")]
    public string Author { get; init; } = string.Empty;

    /// <summary>Comment body.</summary>
    [JsonPropertyName("body")]
    public string Body { get; init; } = string.Empty;

    /// <summary>
    /// Whether this comment is a formal correction request (it transitioned the
    /// record to <see cref="FieldReviewStatus.ChangesRequested"/>).
    /// </summary>
    [JsonPropertyName("correctionRequest")]
    public bool CorrectionRequest { get; init; }

    /// <summary>UTC creation timestamp.</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Filter applied when listing submissions for review.
/// </summary>
public sealed class FieldReviewQuery
{
    /// <summary>Restrict to a single form package id.</summary>
    public string? FormId { get; init; }

    /// <summary>Restrict to a single target service id.</summary>
    public string? ServiceId { get; init; }

    /// <summary>Restrict to a single target layer id.</summary>
    public int? LayerId { get; init; }

    /// <summary>Restrict to a single review status.</summary>
    public string? ReviewStatus { get; init; }

    /// <summary>Restrict to records assigned to a reviewer.</summary>
    public string? AssignedTo { get; init; }

    /// <summary>Restrict to a single sync status.</summary>
    public string? SyncStatus { get; init; }

    /// <summary>When set, restrict to records flagged as conflicting.</summary>
    public bool? HasConflict { get; init; }

    /// <summary>Inclusive lower bound on submission time.</summary>
    public DateTimeOffset? SubmittedFrom { get; init; }

    /// <summary>Inclusive upper bound on submission time.</summary>
    public DateTimeOffset? SubmittedTo { get; init; }

    /// <summary>Maximum number of records to return (server-clamped).</summary>
    public int Limit { get; init; } = 50;

    /// <summary>Zero-based offset for paging.</summary>
    public int Offset { get; init; }
}

/// <summary>
/// Page of submissions returned by a review listing.
/// </summary>
public sealed class FieldSubmissionListResult
{
    /// <summary>Records in this page.</summary>
    [JsonPropertyName("items")]
    public FieldSubmissionRecord[] Items { get; init; } = [];

    /// <summary>Total records matching the filter, ignoring paging.</summary>
    [JsonPropertyName("total")]
    public long Total { get; init; }

    /// <summary>Limit applied to this page.</summary>
    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    /// <summary>Offset applied to this page.</summary>
    [JsonPropertyName("offset")]
    public int Offset { get; init; }
}

/// <summary>
/// Request to assign or unassign a submission to a reviewer.
/// </summary>
public sealed class FieldReviewAssignmentRequest
{
    /// <summary>Reviewer to assign, or null/empty to unassign.</summary>
    [JsonPropertyName("assignedTo")]
    public string? AssignedTo { get; init; }
}

/// <summary>
/// Request to record a review decision (approve / reject / request changes).
/// </summary>
public sealed class FieldReviewDecisionRequest
{
    /// <summary>
    /// Target review status. Must be one of <see cref="FieldReviewStatus.Approved"/>,
    /// <see cref="FieldReviewStatus.Rejected"/>, or
    /// <see cref="FieldReviewStatus.ChangesRequested"/>.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>Optional reviewer note recorded with the decision.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

/// <summary>
/// Request to add a reviewer comment or correction request.
/// </summary>
public sealed class FieldReviewCommentRequest
{
    /// <summary>Comment body.</summary>
    [JsonPropertyName("body")]
    public string Body { get; init; } = string.Empty;

    /// <summary>
    /// When true, the comment is recorded as a correction request and transitions
    /// the record to <see cref="FieldReviewStatus.ChangesRequested"/>.
    /// </summary>
    [JsonPropertyName("correctionRequest")]
    public bool CorrectionRequest { get; init; }
}

/// <summary>
/// Detail view of a submission combining the record, its review comments, and
/// stored attachment metadata.
/// </summary>
public sealed class FieldSubmissionDetail
{
    /// <summary>The submission record and review state.</summary>
    [JsonPropertyName("record")]
    public FieldSubmissionRecord Record { get; init; } = new();

    /// <summary>Reviewer comments and correction requests, oldest first.</summary>
    [JsonPropertyName("comments")]
    public FieldReviewComment[] Comments { get; init; } = [];

    /// <summary>Persisted attachment metadata for this submission.</summary>
    [JsonPropertyName("attachments")]
    public FieldSubmissionAttachmentInfo[] Attachments { get; init; } = [];
}

/// <summary>
/// Sanitized attachment metadata exposed to reviewers.
/// </summary>
public sealed class FieldSubmissionAttachmentInfo
{
    /// <summary>Persisted attachment id, when the upload succeeded.</summary>
    [JsonPropertyName("attachmentId")]
    public long? AttachmentId { get; init; }

    /// <summary>Form field id the attachment was captured for.</summary>
    [JsonPropertyName("fieldId")]
    public string? FieldId { get; init; }

    /// <summary>Original filename.</summary>
    [JsonPropertyName("filename")]
    public string? Filename { get; init; }

    /// <summary>MIME content type.</summary>
    [JsonPropertyName("contentType")]
    public string? ContentType { get; init; }

    /// <summary>Size in bytes.</summary>
    [JsonPropertyName("sizeBytes")]
    public long? SizeBytes { get; init; }

    /// <summary>Persistence status of the attachment.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;
}
