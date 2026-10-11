// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.WorkflowPackages.Domain;

/// <summary>
/// Raised when a version is created from a graph that no longer matches the locked
/// package draft. Derives from <see cref="InvalidOperationException"/> so the console
/// endpoint maps it to HTTP 409.
/// </summary>
public sealed class WorkflowPackageGraphConflictException : InvalidOperationException
{
    /// <summary>
    /// Creates a conflict for <paramref name="packageId"/>.
    /// </summary>
    public WorkflowPackageGraphConflictException(string packageId)
        : base($"Workflow package '{packageId}' changed after validation. Retry version creation.")
    {
        PackageId = packageId;
    }

    /// <summary>
    /// Package whose draft changed.
    /// </summary>
    public string PackageId { get; }
}
