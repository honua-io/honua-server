// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Identity.Domain;

namespace Honua.Core.Features.Identity.Abstractions;

/// <summary>
/// Provisioning store for SCIM 2.0 user lifecycle management (#510). Distinct from
/// <see cref="IUserStore"/>, which exposes only the admin read / role-update / deprovision
/// surface: SCIM additionally requires create, full-replace, and reactivation so an external
/// identity provider (Okta, Microsoft Entra, etc.) owns the user record end to end.
/// </summary>
public interface IScimUserStore
{
    /// <summary>
    /// Provisions a new user or returns <see langword="null"/> when a user with the same
    /// externally supplied identifier already exists.
    /// </summary>
    Task<ManagedUser?> CreateUserAsync(ScimUserProvisioning provisioning, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a provisioned user by identifier.
    /// </summary>
    Task<ManagedUser?> GetUserAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a user by the SCIM <c>userName</c> attribute (case-insensitive). Returns
    /// <see langword="null"/> when no match exists.
    /// </summary>
    Task<ManagedUser?> FindByUserNameAsync(string userName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists provisioned users with SCIM filtering and pagination.
    /// </summary>
    Task<ScimUserPage> ListUsersAsync(ScimUserQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the mutable attributes of an existing user (SCIM PUT semantics). Returns
    /// <see langword="null"/> when the user does not exist.
    /// </summary>
    Task<ManagedUser?> ReplaceUserAsync(string userId, ScimUserProvisioning provisioning, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the active state of a user (SCIM PATCH <c>active</c>). Deactivation revokes
    /// access; reactivation restores it. Returns <see langword="null"/> when the user does
    /// not exist.
    /// </summary>
    Task<ManagedUser?> SetActiveAsync(string userId, bool active, CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently deprovisions (deactivates and clears roles) a user. Returns
    /// <see langword="false"/> when the user does not exist.
    /// </summary>
    Task<bool> DeleteUserAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Attributes supplied by an identity provider when provisioning or replacing a SCIM user.
/// </summary>
public sealed class ScimUserProvisioning
{
    /// <summary>
    /// The SCIM <c>userName</c> — the IdP-owned login identifier. Required.
    /// </summary>
    public required string UserName { get; init; }

    /// <summary>
    /// Display name. Falls back to <see cref="UserName"/> when the IdP omits it.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Primary email address, if supplied.
    /// </summary>
    public string? Email { get; init; }

    /// <summary>
    /// Whether the account is active. Defaults to active when omitted by the IdP.
    /// </summary>
    public bool Active { get; init; } = true;

    /// <summary>
    /// Role names to assign. SCIM group membership is mapped to Honua roles by the caller.
    /// </summary>
    public IReadOnlyList<string> Roles { get; init; } = [];
}

/// <summary>
/// SCIM list query parameters (1-based <c>startIndex</c> per RFC 7644).
/// </summary>
public sealed class ScimUserQuery
{
    /// <summary>
    /// Optional <c>userName eq "..."</c> equality filter value.
    /// </summary>
    public string? UserNameEquals { get; init; }

    /// <summary>
    /// 1-based index of the first result to return.
    /// </summary>
    public int StartIndex { get; init; } = 1;

    /// <summary>
    /// Maximum number of results to return in this page.
    /// </summary>
    public int Count { get; init; } = 100;
}

/// <summary>
/// A page of SCIM users together with the total match count (for <c>totalResults</c>).
/// </summary>
public sealed class ScimUserPage
{
    /// <summary>
    /// Users in this page.
    /// </summary>
    public required IReadOnlyList<ManagedUser> Users { get; init; }

    /// <summary>
    /// Total number of users matching the query (before pagination).
    /// </summary>
    public required int TotalCount { get; init; }
}
