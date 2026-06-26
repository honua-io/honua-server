// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.MultiTenancy.Domain;

/// <summary>
/// Lifecycle state of a provisioned tenant (issue #2156).
/// </summary>
public enum TenantStatus
{
    /// <summary>The tenant is provisioned and may read and write.</summary>
    Active = 0,

    /// <summary>
    /// The tenant is suspended: access (read and write) is blocked, but its data and
    /// configuration are retained so it can be resumed to its prior state.
    /// </summary>
    Suspended = 1,

    /// <summary>
    /// The tenant has been deleted/retired. Access is blocked and the tenant is no longer
    /// resumable; data removal follows the documented retirement policy.
    /// </summary>
    Deleted = 2,
}
