// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Identity.Domain;

namespace Honua.Core.Features.Identity.Abstractions;

/// <summary>
/// Store for managed user identities.
/// </summary>
public interface IUserStore
{
    /// <summary>
    /// Lists users with optional filtering.
    /// </summary>
    Task<UserListResult> ListUsersAsync(UserListFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific user by ID.
    /// </summary>
    Task<ManagedUser?> GetUserAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates role assignments for a user.
    /// </summary>
    Task<ManagedUser?> UpdateUserRolesAsync(string userId, IReadOnlyList<string> roles, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deprovisions (deactivates) a user.
    /// </summary>
    Task<bool> DeprovisionUserAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Filter criteria for listing users.
/// </summary>
public sealed class UserListFilter
{
    /// <summary>
    /// Filter by provisioning source.
    /// </summary>
    public string? ProvisioningSource { get; init; }

    /// <summary>
    /// Filter by role membership.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>
    /// Filter by active status.
    /// </summary>
    public bool? IsActive { get; init; }

    /// <summary>
    /// Maximum number of results to return.
    /// </summary>
    public int Limit { get; init; } = 100;

    /// <summary>
    /// Number of results to skip for pagination.
    /// </summary>
    public int Offset { get; init; }
}

/// <summary>
/// Paginated result of user listing.
/// </summary>
public sealed class UserListResult
{
    /// <summary>
    /// Users matching the filter criteria.
    /// </summary>
    public required IReadOnlyList<ManagedUser> Users { get; init; }

    /// <summary>
    /// Total number of users matching the filter (before pagination).
    /// </summary>
    public required int TotalCount { get; init; }
}
