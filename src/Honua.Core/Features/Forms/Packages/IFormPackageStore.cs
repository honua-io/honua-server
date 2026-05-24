// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Forms.Packages;

/// <summary>
/// Durable store for server-owned, versioned form packages and submission records.
/// </summary>
public interface IFormPackageStore
{
    /// <summary>
    /// Lists package families.
    /// </summary>
    Task<FormPackageSummary[]> ListPackagesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current package version for a lifecycle status.
    /// </summary>
    Task<FormPackageVersion?> GetCurrentVersionAsync(
        string formId,
        string status,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets one package version by id and version number.
    /// </summary>
    Task<FormPackageVersion?> GetVersionAsync(
        string formId,
        int version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all versions for a package id.
    /// </summary>
    Task<FormPackageVersion[]> ListVersionsAsync(
        string formId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a new draft package version.
    /// </summary>
    Task<FormPackageVersion> SaveDraftAsync(
        FormPackageDocument package,
        string actor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces an existing draft version when the expected ETag matches.
    /// </summary>
    Task<FormPackageVersion?> UpdateDraftAsync(
        string formId,
        int version,
        FormPackageDocument package,
        string expectedETag,
        string actor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the latest validation result for a draft version.
    /// </summary>
    Task<FormPackageVersion?> StoreValidationAsync(
        string formId,
        int version,
        FormPackageValidationResult validation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Promotes a draft version to published, making that version immutable.
    /// </summary>
    Task<FormPackageVersion?> PublishAsync(
        string formId,
        int version,
        FormPackageValidationResult validation,
        string actor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new draft version from a published package version.
    /// </summary>
    Task<FormPackageVersion?> ReopenAsync(
        string formId,
        int publishedVersion,
        string actor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up a terminal or pending submission by idempotency key.
    /// </summary>
    Task<FormSubmissionRecord?> GetSubmissionByIdempotencyAsync(
        string formId,
        int formVersion,
        string actorHash,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a pending submission record.
    /// </summary>
    Task CreateSubmissionAsync(
        Guid submissionId,
        string? idempotencyKey,
        string actorHash,
        string requestHash,
        FormPackageVersion packageVersion,
        FormSubmissionRequest request,
        string status,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a submission record terminal and stores the response payload.
    /// </summary>
    Task CompleteSubmissionAsync(
        Guid submissionId,
        FormSubmissionResponse response,
        string status,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the attachment policy and persistence outcome for a submission attachment.
    /// </summary>
    Task RecordAttachmentOutcomeAsync(
        Guid submissionId,
        FormSubmissionAttachmentDescriptor descriptor,
        FormSubmissionAttachmentOutcome outcome,
        FormPackageVersion packageVersion,
        CancellationToken cancellationToken = default);
}
