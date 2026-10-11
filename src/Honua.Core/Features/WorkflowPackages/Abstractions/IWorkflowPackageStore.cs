// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Core.Features.WorkflowPackages.Abstractions;

/// <summary>
/// Store for workflow package drafts, immutable versions, and publications.
/// </summary>
public interface IWorkflowPackageStore
{
    /// <summary>
    /// Lists package drafts.
    /// </summary>
    Task<IReadOnlyList<WorkflowPackage>> ListPackagesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a package draft by identifier.
    /// </summary>
    Task<WorkflowPackage?> GetPackageAsync(string packageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or replaces a package draft.
    /// </summary>
    Task<WorkflowPackage> SavePackageAsync(WorkflowPackage package, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an immutable package version from <paramref name="validatedGraph"/>.
    /// Implementations must lock the package draft and reject the call when that
    /// draft's hash no longer equals <paramref name="packageHash"/>, and must store
    /// <paramref name="validatedGraph"/> rather than whatever draft is current.
    /// </summary>
    Task<WorkflowPackageVersion> CreateVersionAsync(
        string packageId,
        string packageHash,
        WorkflowGraph validatedGraph,
        WorkflowPackageValidationResult validation,
        string? createdBy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves an immutable package version.
    /// </summary>
    Task<WorkflowPackageVersion?> GetVersionAsync(
        string packageId,
        int version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists immutable package versions.
    /// </summary>
    Task<IReadOnlyList<WorkflowPackageVersion>> ListVersionsAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a publication record.
    /// </summary>
    Task<WorkflowPublication> SavePublicationAsync(
        WorkflowPublication publication,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a publication record.
    /// </summary>
    Task<WorkflowPublication?> GetPublicationAsync(
        string publicationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists publications for a package or all packages.
    /// </summary>
    Task<IReadOnlyList<WorkflowPublication>> ListPublicationsAsync(
        string? packageId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a publication.
    /// </summary>
    /// <returns>The deleted publication, or <see langword="null"/> when it did not exist.</returns>
    Task<WorkflowPublication?> DeletePublicationAsync(
        string publicationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the lifecycle status of an existing publication.
    /// </summary>
    /// <returns>The updated publication, or <see langword="null"/> when it did not exist.</returns>
    Task<WorkflowPublication?> SetPublicationStatusAsync(
        string publicationId,
        WorkflowPublicationStatus status,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a publication's scheduler definition id when it still equals
    /// <paramref name="expectedDefinitionId"/>. Returns false when the row is missing
    /// or the id has already changed, so a legacy shared id can be split once.
    /// </summary>
    Task<bool> TryReplaceWorkflowDefinitionIdAsync(
        string publicationId,
        string expectedDefinitionId,
        string replacementDefinitionId,
        CancellationToken cancellationToken = default);
}
