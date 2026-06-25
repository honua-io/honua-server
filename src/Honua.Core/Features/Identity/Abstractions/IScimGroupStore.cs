// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Identity.Domain;

namespace Honua.Core.Features.Identity.Abstractions;

/// <summary>
/// Provisioning store for SCIM 2.0 group lifecycle management (#510). A SCIM group maps to
/// a Honua role: members of the group receive the role, so deprovisioning a member (or the
/// group) removes the corresponding role assignment and the access it confers.
/// </summary>
public interface IScimGroupStore
{
    /// <summary>
    /// Provisions a new group (mapped to a role) or returns <see langword="null"/> when a
    /// group with the same display name already exists.
    /// </summary>
    Task<ScimGroup?> CreateGroupAsync(ScimGroupProvisioning provisioning, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a provisioned group by identifier.
    /// </summary>
    Task<ScimGroup?> GetGroupAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists provisioned groups with SCIM filtering and pagination.
    /// </summary>
    Task<ScimGroupPage> ListGroupsAsync(ScimGroupQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a group's display name and full member set (SCIM PUT semantics). Returns
    /// <see langword="null"/> when the group does not exist.
    /// </summary>
    Task<ScimGroup?> ReplaceGroupAsync(string groupId, ScimGroupProvisioning provisioning, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or removes members of a group (SCIM PATCH <c>members</c>). Adding a member grants
    /// the mapped role; removing a member revokes it. Returns <see langword="null"/> when the
    /// group does not exist.
    /// </summary>
    Task<ScimGroup?> UpdateMembersAsync(string groupId, ScimGroupMemberChange change, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a group and revokes the mapped role from all of its members. Returns
    /// <see langword="false"/> when the group does not exist.
    /// </summary>
    Task<bool> DeleteGroupAsync(string groupId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Attributes supplied by an identity provider when provisioning or replacing a SCIM group.
/// </summary>
public sealed class ScimGroupProvisioning
{
    /// <summary>
    /// The SCIM group <c>displayName</c>; also the Honua role name the group maps to. Required.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// User identifiers that are members of the group.
    /// </summary>
    public IReadOnlyList<string> MemberUserIds { get; init; } = [];
}

/// <summary>
/// A SCIM group PATCH member delta. Members are added or removed; the operation is not a
/// full replace.
/// </summary>
public sealed class ScimGroupMemberChange
{
    /// <summary>
    /// User identifiers to add to the group.
    /// </summary>
    public IReadOnlyList<string> Add { get; init; } = [];

    /// <summary>
    /// User identifiers to remove from the group.
    /// </summary>
    public IReadOnlyList<string> Remove { get; init; } = [];
}

/// <summary>
/// SCIM list query parameters for groups (1-based <c>startIndex</c> per RFC 7644).
/// </summary>
public sealed class ScimGroupQuery
{
    /// <summary>
    /// Optional <c>displayName eq "..."</c> equality filter value.
    /// </summary>
    public string? DisplayNameEquals { get; init; }

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
/// A page of SCIM groups together with the total match count.
/// </summary>
public sealed class ScimGroupPage
{
    /// <summary>
    /// Groups in this page.
    /// </summary>
    public required IReadOnlyList<ScimGroup> Groups { get; init; }

    /// <summary>
    /// Total number of groups matching the query (before pagination).
    /// </summary>
    public required int TotalCount { get; init; }
}
