// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Metadata.Abstractions;

/// <summary>
/// Service layer for semantic metadata inventories, environment bindings, and release packages.
/// </summary>
public interface IMetadataReleaseService
{
    /// <summary>
    /// Returns semantic inventory for an environment, or null when the environment has no active snapshot.
    /// </summary>
    Task<MetadataSemanticInventoryResponse?> GetSemanticInventoryAsync(
        string environment,
        MetadataSemanticInventoryFilter filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns binding summaries for semantic artifacts across environments.
    /// </summary>
    Task<MetadataEnvironmentBindingsResponse> GetEnvironmentBindingsAsync(
        MetadataEnvironmentBindingsRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates and persists a metadata release package.
    /// </summary>
    Task<MetadataReleasePackage> CreateReleasePackageAsync(
        CreateMetadataReleasePackageRequest request,
        string createdBy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Emits and persists a metadata release package for a published workflow / geoprocessing
    /// package version. The workflow is recorded as a single
    /// <see cref="Domain.V2.MetadataSemanticArtifactKind.Workflow"/> entry without resolving it
    /// against a Metadata v2 graph snapshot, so the downstream GitOps changeset builder can promote
    /// the published workflow. A workflow publish is additive
    /// (<see cref="Domain.V2.MetadataReleaseChangeClass.Content"/>).
    /// </summary>
    Task<MetadataReleasePackage> CreateWorkflowReleasePackageAsync(
        CreateWorkflowReleasePackageRequest request,
        string createdBy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a persisted metadata release package.
    /// </summary>
    Task<MetadataReleasePackage?> GetReleasePackageAsync(
        Guid packageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists release-package summaries (newest first) for the GitOps releases surface.
    /// </summary>
    Task<MetadataReleasePackageListResponse> ListReleasePackagesAsync(
        MetadataReleasePackageListFilter filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports a persisted metadata release package as a GitOps-safe manifest.
    /// </summary>
    Task<GitOpsMetadataReleaseManifest?> GetGitOpsManifestAsync(
        Guid packageId,
        CancellationToken cancellationToken = default);
}
