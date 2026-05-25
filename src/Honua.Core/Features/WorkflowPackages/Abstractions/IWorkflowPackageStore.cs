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
    /// Creates an immutable package version.
    /// </summary>
    Task<WorkflowPackageVersion> CreateVersionAsync(
        string packageId,
        string packageHash,
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
}
