// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Metadata.Abstractions;

public interface IMetadataReleaseService
{
    Task<MetadataSemanticInventoryResponse?> GetSemanticInventoryAsync(
        string environment,
        MetadataSemanticInventoryFilter filter,
        CancellationToken cancellationToken = default);

    Task<MetadataEnvironmentBindingsResponse> GetEnvironmentBindingsAsync(
        MetadataEnvironmentBindingsRequest request,
        CancellationToken cancellationToken = default);

    Task<MetadataReleasePackage> CreateReleasePackageAsync(
        CreateMetadataReleasePackageRequest request,
        string createdBy,
        CancellationToken cancellationToken = default);

    Task<MetadataReleasePackage> CreateWorkflowReleasePackageAsync(
        CreateWorkflowReleasePackageRequest request,
        string createdBy,
        CancellationToken cancellationToken = default);

    Task<MetadataReleasePackage?> GetReleasePackageAsync(
        Guid packageId,
        CancellationToken cancellationToken = default);

    Task<MetadataReleasePackageListResponse> ListReleasePackagesAsync(
        MetadataReleasePackageListFilter filter,
        CancellationToken cancellationToken = default);

    Task<GitOpsMetadataReleaseManifest?> GetGitOpsManifestAsync(
        Guid packageId,
        CancellationToken cancellationToken = default);
}
