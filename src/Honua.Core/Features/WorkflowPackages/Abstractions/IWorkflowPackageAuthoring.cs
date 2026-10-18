// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Core.Features.WorkflowPackages.Abstractions;

/// <summary>Principal-aware built-in workflow authoring shared by protocol adapters.</summary>
public interface IWorkflowPackageAuthoring
{
    /// <summary>Lists only packages visible to the actual caller and freshly authorized workspace.</summary>
    Task<IReadOnlyList<WorkflowPackage>> ListAsync(ClaimsPrincipal principal, string workspaceId, string turnId, CancellationToken cancellationToken);

    /// <summary>Reads an immutable version without disclosing other owners' resources.</summary>
    Task<WorkflowPackageVersion?> ReadAsync(ClaimsPrincipal principal, string workspaceId, string turnId, string packageId, int version, CancellationToken cancellationToken);

    /// <summary>Creates a new package and validated immutable version; never modifies a previous package or executes the graph.</summary>
    Task<WorkflowPackageVersion> CreateAsync(ClaimsPrincipal principal, string workspaceId, string turnId, string name, WorkflowGraph graph, CancellationToken cancellationToken);
}
