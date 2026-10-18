// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Geoprocessing;

namespace Honua.Server.Features.WorkflowPackages;

/// <summary>Thin immutable authoring adapter over canonical package validation and persistence.</summary>
internal sealed class WorkflowPackageAuthoring(WorkflowPackageService packages, WorkflowPackageAuthorization authorization)
    : IWorkflowPackageAuthoring
{
    public async Task<IReadOnlyList<WorkflowPackage>> ListAsync(ClaimsPrincipal principal, string workspaceId, string turnId,
        CancellationToken cancellationToken)
    {
        await RequireWorkspaceAsync(principal, workspaceId, turnId, false, cancellationToken).ConfigureAwait(false);
        return await packages.ListPackagesAsync(principal, cancellationToken, workspaceId, turnId).ConfigureAwait(false);
    }

    public async Task<WorkflowPackageVersion?> ReadAsync(ClaimsPrincipal principal, string workspaceId, string turnId,
        string packageId, int version, CancellationToken cancellationToken)
    {
        await RequireWorkspaceAsync(principal, workspaceId, turnId, false, cancellationToken).ConfigureAwait(false);
        return await packages.GetVersionAsync(packageId, version, principal, cancellationToken, workspaceId, turnId).ConfigureAwait(false);
    }

    public async Task<WorkflowPackageVersion> CreateAsync(ClaimsPrincipal principal, string workspaceId, string turnId,
        string name, WorkflowGraph graph, CancellationToken cancellationToken)
    {
        await RequireWorkspaceAsync(principal, workspaceId, turnId, true, cancellationToken).ConfigureAwait(false);
        // No caller-selected ID or upsert: an edit always produces a new package.
        var package = await packages.SavePackageAsync(new()
        {
            PackageId = $"agent-workflow-{Guid.NewGuid():N}", Name = name, Graph = graph,
        }, principal, cancellationToken, workspaceId, turnId).ConfigureAwait(false);
        // Canonical validation supplies the content hash and immutable snapshot. A validation
        // failure leaves a draft, never an executable/publication or a false success receipt.
        return await packages.CreateVersionAsync(package.PackageId, principal, cancellationToken, workspaceId, turnId).ConfigureAwait(false);
    }

    private async Task RequireWorkspaceAsync(ClaimsPrincipal principal, string workspaceId, string turnId, bool write,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(turnId))
            throw new GeoprocessingValidationException("An explicit workspace and frozen turn are required.");
        var access = await authorization.ResolveAsync(principal, cancellationToken, workspaceId, turnId, write).ConfigureAwait(false);
        if (!access.WorkspaceAgent || string.IsNullOrWhiteSpace(access.DelegatedOwnerActorId))
            throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
    }
}
