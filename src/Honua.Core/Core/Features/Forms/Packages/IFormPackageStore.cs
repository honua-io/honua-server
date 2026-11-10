// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Forms.Packages;

public interface IFormPackageStore
{
    Task<FormPackageSummary[]> ListPackagesAsync(CancellationToken cancellationToken = default);

    Task<FormPackageVersion?> GetCurrentVersionAsync(
        string formId,
        string status,
        CancellationToken cancellationToken = default);

    Task<FormPackageVersion?> GetVersionAsync(
        string formId,
        int version,
        CancellationToken cancellationToken = default);

    Task<FormPackageVersion[]> ListVersionsAsync(
        string formId,
        CancellationToken cancellationToken = default);

    Task<FormPackageVersion> SaveDraftAsync(
        FormPackageDocument package,
        string actor,
        CancellationToken cancellationToken = default);

    Task<FormPackageVersion?> UpdateDraftAsync(
        string formId,
        int version,
        FormPackageDocument package,
        string expectedETag,
        string actor,
        CancellationToken cancellationToken = default);

    Task<FormPackageVersion?> StoreValidationAsync(
        string formId,
        int version,
        FormPackageValidationResult validation,
        CancellationToken cancellationToken = default);

    Task<FormPackageVersion?> PublishAsync(
        string formId,
        int version,
        FormPackageValidationResult validation,
        string actor,
        CancellationToken cancellationToken = default);

    Task<FormPackageVersion?> ReopenAsync(
        string formId,
        int publishedVersion,
        string actor,
        CancellationToken cancellationToken = default);

    Task<FormSubmissionRecord?> GetSubmissionByIdempotencyAsync(
        string formId,
        int formVersion,
        string actorHash,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<bool> CreateSubmissionAsync(
        Guid submissionId,
        string? idempotencyKey,
        string actorHash,
        string requestHash,
        FormPackageVersion packageVersion,
        FormSubmissionRequest request,
        string status,
        CancellationToken cancellationToken = default);

    Task CompleteSubmissionAsync(
        Guid submissionId,
        FormSubmissionResponse response,
        string status,
        CancellationToken cancellationToken = default);

    Task RecordAttachmentOutcomeAsync(
        Guid submissionId,
        FormSubmissionAttachmentDescriptor descriptor,
        FormSubmissionAttachmentOutcome outcome,
        FormPackageVersion packageVersion,
        CancellationToken cancellationToken = default);

    // Only a transient submission may be deleted so its idempotency key can be reused; a terminal outcome stays as the permanent record.
    Task<bool> DeleteSubmissionAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default);
}
