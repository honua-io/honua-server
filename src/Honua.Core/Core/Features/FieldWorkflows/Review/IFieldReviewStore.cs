// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FieldWorkflows.Review;

public interface IFieldReviewStore
{
    Task<FieldSubmissionListResult> ListSubmissionsAsync(
        FieldReviewQuery query,
        CancellationToken cancellationToken = default);

    Task<FieldSubmissionDetail?> GetSubmissionAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default);

    Task<FieldReviewState?> AssignAsync(
        Guid submissionId,
        string? assignedTo,
        string actor,
        CancellationToken cancellationToken = default);

    Task<FieldReviewState?> RecordDecisionAsync(
        Guid submissionId,
        string status,
        string? note,
        string? expectedETag,
        string actor,
        CancellationToken cancellationToken = default);

    Task<FieldReviewComment?> AddCommentAsync(
        Guid submissionId,
        string body,
        bool correctionRequest,
        string actor,
        CancellationToken cancellationToken = default);
}

public sealed class FieldReviewConcurrencyException : Exception
{
    public FieldReviewConcurrencyException()
        : base("The review state was modified concurrently.")
    {
    }

    public FieldReviewConcurrencyException(string message)
        : base(message)
    {
    }

    public FieldReviewConcurrencyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
