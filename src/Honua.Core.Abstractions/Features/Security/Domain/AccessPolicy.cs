// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Domain;

/// <summary>
/// Authorization policy applied to a resource (service, layer, dataset, publication).
/// Used by <c>IAccessPolicyEvaluator</c> + <c>AccessPolicyHelpers</c> to decide whether
/// a request principal is allowed at a given <c>AccessScope</c>.
/// </summary>
public sealed record AccessPolicy
{
    /// <summary>
    /// When true, anonymous access is allowed regardless of other constraints.
    /// </summary>
    public bool AllowAnonymous { get; init; }

    /// <summary>
    /// When true, anonymous write access is allowed regardless of other constraints.
    /// </summary>
    public bool AllowAnonymousWrite { get; init; }

    /// <summary>
    /// Allowed role names for access (case-insensitive).
    /// </summary>
    public string[]? AllowedRoles { get; init; }

    /// <summary>
    /// Allowed role names for write access (case-insensitive).
    /// Falls back to <see cref="AllowedRoles"/> when not specified.
    /// </summary>
    public string[]? AllowedWriteRoles { get; init; }
}
