// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FieldWorkflows.Review;

/// <summary>
/// Durable store for back-office review and QA over mobile-submitted field
/// records. Read access projects durable form submissions; write access manages
/// server-owned review state (status, assignment, comments, correction requests)
/// without mutating the underlying submission.
/// </summary>
public interface IFieldReviewStore
{
    /// <summary>
    /// Lists submissions matching the supplied review filter, ordered by
    /// submission time descending.
    /// </summary>
    Task<FieldSubmissionListResult> ListSubmissionsAsync(
        FieldReviewQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a single submission with its review state, comments, and attachment
    /// metadata, or <see langword="null"/> when not found.
    /// </summary>
    Task<FieldSubmissionDetail?> GetSubmissionAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Assigns or unassigns a submission to a reviewer, transitioning a pending
    /// record to <c>in_review</c> on assignment. Returns the updated review state,
    /// or <see langword="null"/> when the submission does not exist.
    /// </summary>
    Task<FieldReviewState?> AssignAsync(
        Guid submissionId,
        string? assignedTo,
        string actor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a review decision (approve / reject / request changes) using
    /// optimistic concurrency. Returns the updated review state; returns
    /// <see langword="null"/> when the submission does not exist, and throws
    /// <see cref="FieldReviewConcurrencyException"/> when the expected ETag does
    /// not match.
    /// </summary>
    Task<FieldReviewState?> RecordDecisionAsync(
        Guid submissionId,
        string status,
        string? note,
        string? expectedETag,
        string actor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a reviewer comment or correction request. When
    /// <paramref name="correctionRequest"/> is set the record transitions to
    /// <c>changes_requested</c>. Returns the stored comment, or
    /// <see langword="null"/> when the submission does not exist.
    /// </summary>
    Task<FieldReviewComment?> AddCommentAsync(
        Guid submissionId,
        string body,
        bool correctionRequest,
        string actor,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Thrown when a review decision is attempted with a stale optimistic-concurrency
/// token.
/// </summary>
public sealed class FieldReviewConcurrencyException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="FieldReviewConcurrencyException"/> class.</summary>
    public FieldReviewConcurrencyException()
        : base("The review state was modified concurrently.")
    {
    }

    /// <summary>Initializes a new instance with a custom message.</summary>
    /// <param name="message">The exception message.</param>
    public FieldReviewConcurrencyException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with a custom message and inner exception.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public FieldReviewConcurrencyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
