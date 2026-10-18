// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Core.Features.WorkflowPackages.Abstractions;

/// <summary>Canonical durable workflow publication governance, separate from real-time graph editing.</summary>
public interface IWorkflowPackageGovernance
{
    /// <summary>Proposes an exact immutable workflow transition for distinct human approval.</summary>
    Task<OperationHandle> ProposeAsync(ClaimsPrincipal principal, string workspaceId, string turnId,
        WorkflowPackageGovernanceIntent intent, CancellationToken cancellationToken);
}

/// <summary>Untrusted requested transition; owner, credentials and approval proof are server-derived.</summary>
public sealed record WorkflowPackageGovernanceIntent
{
    /// <summary>Supported transition: publish, delete, or set-status.</summary>
    public required string Action { get; init; }
    /// <summary>Exact package identifier.</summary>
    public required string PackageId { get; init; }
    /// <summary>Immutable package version.</summary>
    public required int Version { get; init; }
    /// <summary>Exact immutable content hash to review.</summary>
    public required string PackageHash { get; init; }
    /// <summary>Publication to create or change.</summary>
    public required string PublicationId { get; init; }
    /// <summary>Publication kind when publishing.</summary>
    public WorkflowPublicationTarget Target { get; init; }
    /// <summary>Optional published process endpoint identity.</summary>
    public string? ProcessId { get; init; }
    /// <summary>Optional publication schedule.</summary>
    public WorkflowSchedule? Schedule { get; init; }
    /// <summary>Whether a new publication is enabled.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>Desired lifecycle state for set-status.</summary>
    public WorkflowPublicationStatus Status { get; init; }
}
