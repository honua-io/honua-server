// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Core.Features.WorkflowPackages.Abstractions;

public interface IWorkflowPackageStore
{
    Task<IReadOnlyList<WorkflowPackage>> ListPackagesAsync(CancellationToken cancellationToken = default);

    Task<WorkflowPackage?> GetPackageAsync(string packageId, CancellationToken cancellationToken = default);

    Task<WorkflowPackage> SavePackageAsync(WorkflowPackage package, CancellationToken cancellationToken = default);

    // Version creation locks the draft and stores the validated hash, rejecting the call when the draft hash has changed.
    Task<WorkflowPackageVersion> CreateVersionAsync(
        string packageId,
        string packageHash,
        WorkflowGraph validatedGraph,
        WorkflowPackageValidationResult validation,
        string? createdBy,
        CancellationToken cancellationToken = default);

    Task<WorkflowPackageVersion?> GetVersionAsync(
        string packageId,
        int version,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowPackageVersion>> ListVersionsAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    Task<WorkflowPublication> SavePublicationAsync(
        WorkflowPublication publication,
        CancellationToken cancellationToken = default);

    Task<WorkflowPublication?> GetPublicationAsync(
        string publicationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowPublication>> ListPublicationsAsync(
        string? packageId = null,
        CancellationToken cancellationToken = default);

    Task<WorkflowPublication?> DeletePublicationAsync(
        string publicationId,
        CancellationToken cancellationToken = default);

    Task<WorkflowPublication?> SetPublicationStatusAsync(
        string publicationId,
        WorkflowPublicationStatus status,
        CancellationToken cancellationToken = default);

    Task<bool> TryReplaceWorkflowDefinitionIdAsync(
        string publicationId,
        string expectedDefinitionId,
        string replacementDefinitionId,
        CancellationToken cancellationToken = default);
}
