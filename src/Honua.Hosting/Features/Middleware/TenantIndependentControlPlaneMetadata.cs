// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Middleware;

/// <summary>
/// Marks a control-plane endpoint whose data is global rather than tenant-scoped, allowing a
/// validated external bearer to reach endpoint authorization without an effective tenant.
/// </summary>
public sealed class TenantIndependentControlPlaneMetadata
{
    /// <summary>
    /// Gets the shared endpoint metadata marker.
    /// </summary>
    public static TenantIndependentControlPlaneMetadata Instance { get; } = new();

    private TenantIndependentControlPlaneMetadata()
    {
    }
}
