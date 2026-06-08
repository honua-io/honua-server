// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Console.Models;

// Console Access (RBAC) read projection consumed by the console Settings > Access surface.
//
// The console (Honua.Console.Contracts/RbacAccessShims.cs) calls
//   GET /api/v1/console/access/{workspaceId}/roles    -> ConsoleRbacOverview
//   GET /api/v1/console/access/{workspaceId}/members  -> ConsoleTeamMembership
// and deserializes the camelCase JSON shapes mirrored below. The field names here MUST match the
// [JsonPropertyName] names on the console wire records (HonuaConsoleRbacOverview / HonuaConsoleTeamMembership)
// so the contract binds.
//
// PROJECTION NOTE (mapping): honua-server#1162's first-class workspace-scoped RBAC model (the
// ConsoleAccessScope hierarchy, per-workspace role memberships, and pending invitations) is not present in
// this server build — only the GLOBAL admin RBAC store (IRoleStore: role definitions + service/layer/operation
// permission grants) exists. These endpoints therefore PROJECT the global admin roles into the workspace-scoped
// console Access shape:
//   - Each global RoleDefinition becomes one ConsoleRbacRole row (IsCustom = !IsBuiltIn). Its
//     service/layer/operation PermissionGrants are folded into the fixed console permission columns
//     (manage-content, manage-roles, share, publish, view) so the role x permission matrix renders honestly
//     from the real grants rather than fabricated data.
//   - The scope hierarchy strip is the static, server-owned scope vocabulary (workspace > environment >
//     content > publication > resource-field). Global roles are surfaced for the workspace at the workspace scope.
//   - Membership: there is NO server-side workspace-membership roster or invitation store in this build, so the
//     members read returns an honest empty-but-valid roster (200, zero counts) rather than inventing members.
// When honua-server ships the first-class workspace RBAC model (#1162), back these reads with that store.

/// <summary>One level of the Console RBAC scope hierarchy (workspace > environment > content > publication > field).</summary>
public sealed record ConsoleAccessScope
{
    /// <summary>Scope level key (e.g. "workspace").</summary>
    public required string Level { get; init; }

    /// <summary>Human-readable scope label.</summary>
    public required string Label { get; init; }

    /// <summary>Optional description of what the scope governs.</summary>
    public string? Description { get; init; }
}

/// <summary>One permission column in the role x permission matrix.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Mirrors the console Console Access wire record HonuaConsoleRbacPermission (honua-server#1162 contract).")]
public sealed record ConsoleRbacPermission
{
    /// <summary>Permission key (e.g. "manage-content").</summary>
    public required string Key { get; init; }

    /// <summary>Human-readable permission label.</summary>
    public required string Label { get; init; }
}

/// <summary>How a role grants a single permission, with an optional qualifier label.</summary>
public sealed record ConsoleRbacGrant
{
    /// <summary>Permission key this grant addresses.</summary>
    public required string Permission { get; init; }

    /// <summary>Grant kind: granted | env-scoped | scoped | custom | not-granted.</summary>
    public required string Grant { get; init; }

    /// <summary>Optional qualifier (e.g. "read-only").</summary>
    public string? Qualifier { get; init; }
}

/// <summary>One role row in the matrix (built-in or custom) with its per-permission grants.</summary>
public sealed record ConsoleRbacRole
{
    /// <summary>Role id.</summary>
    public required string Id { get; init; }

    /// <summary>Role name.</summary>
    public required string Name { get; init; }

    /// <summary>Optional role description.</summary>
    public string? Description { get; init; }

    /// <summary>Whether the role is custom (true) or built-in (false).</summary>
    public bool IsCustom { get; init; }

    /// <summary>Per-permission grants for this role.</summary>
    public IReadOnlyList<ConsoleRbacGrant> Grants { get; init; } = [];
}

/// <summary>The Console Access (RBAC) roles overview projection for a workspace.</summary>
public sealed record ConsoleRbacOverview
{
    /// <summary>Workspace id this overview was projected for.</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>Optional workspace display name.</summary>
    public string? WorkspaceName { get; init; }

    /// <summary>The scope hierarchy strip.</summary>
    public IReadOnlyList<ConsoleAccessScope> Scopes { get; init; } = [];

    /// <summary>The permission columns.</summary>
    public IReadOnlyList<ConsoleRbacPermission> Permissions { get; init; } = [];

    /// <summary>The role rows with their grants.</summary>
    public IReadOnlyList<ConsoleRbacRole> Roles { get; init; } = [];

    /// <summary>Count of built-in roles.</summary>
    public int BuiltInRoleCount { get; init; }

    /// <summary>Count of custom roles.</summary>
    public int CustomRoleCount { get; init; }

    /// <summary>Number of members affected by these roles (0 until membership is tracked server-side).</summary>
    public int MembersAffected { get; init; }

    /// <summary>Whether the caller may author/clone roles.</summary>
    public bool CanManageRoles { get; init; }
}

/// <summary>A workspace member row: identity, role, scope, last-active, and identity source.</summary>
public sealed record ConsoleTeamMember
{
    /// <summary>Member id.</summary>
    public required string Id { get; init; }

    /// <summary>Display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Optional identity (email/login).</summary>
    public string? Identity { get; init; }

    /// <summary>Optional role id.</summary>
    public string? RoleId { get; init; }

    /// <summary>Role name.</summary>
    public required string RoleName { get; init; }

    /// <summary>Whether the member's role is custom.</summary>
    public bool IsCustomRole { get; init; }

    /// <summary>Optional scope label.</summary>
    public string? Scope { get; init; }

    /// <summary>Optional last-active timestamp.</summary>
    public string? LastActive { get; init; }

    /// <summary>Optional identity source.</summary>
    public string? Source { get; init; }

    /// <summary>Principal kind: "user", "api-key", or "group".</summary>
    public string? PrincipalKind { get; init; }
}

/// <summary>A pending invitation row awaiting acceptance.</summary>
public sealed record ConsoleTeamInvitation
{
    /// <summary>Invitation id.</summary>
    public required string Id { get; init; }

    /// <summary>Invited identity.</summary>
    public required string Identity { get; init; }

    /// <summary>Role name to be granted on acceptance.</summary>
    public required string RoleName { get; init; }

    /// <summary>Whether the role is custom.</summary>
    public bool IsCustomRole { get; init; }

    /// <summary>Optional scope label.</summary>
    public string? Scope { get; init; }

    /// <summary>Optional invitation status.</summary>
    public string? Status { get; init; }
}

/// <summary>The Console Access membership projection for a workspace.</summary>
public sealed record ConsoleTeamMembership
{
    /// <summary>Workspace id.</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>Optional workspace display name.</summary>
    public string? WorkspaceName { get; init; }

    /// <summary>The member roster.</summary>
    public IReadOnlyList<ConsoleTeamMember> Members { get; init; } = [];

    /// <summary>The pending invitations.</summary>
    public IReadOnlyList<ConsoleTeamInvitation> Invitations { get; init; } = [];

    /// <summary>Active member count.</summary>
    public int ActiveCount { get; init; }

    /// <summary>Pending invitation count.</summary>
    public int PendingCount { get; init; }

    /// <summary>Deactivated member count.</summary>
    public int DeactivatedCount { get; init; }

    /// <summary>Whether the caller may issue invitations.</summary>
    public bool CanInvite { get; init; }
}

/// <summary>
/// Request body to create or update a custom Console Access role (honua-server#1162).
/// The grants use the same fixed console permission columns the overview exposes; each non-"not-granted"
/// grant is folded into a wildcard service/layer permission grant for the mapped admin operation.
/// </summary>
public sealed record ConsoleRoleWriteRequest
{
    /// <summary>Role display name (required).</summary>
    public required string Name { get; init; }

    /// <summary>Optional role description.</summary>
    public string? Description { get; init; }

    /// <summary>Per-permission grants for the role.</summary>
    public IReadOnlyList<ConsoleRbacGrant> Grants { get; init; } = [];
}

/// <summary>One role-change audit entry projected from the server audit log (resourceType "role").</summary>
public sealed record ConsoleRoleAuditEntry
{
    /// <summary>Audit row id.</summary>
    public required long Id { get; init; }

    /// <summary>ISO-8601 timestamp of the change.</summary>
    public required string Timestamp { get; init; }

    /// <summary>Actor who made the change (user id, api-key, or "anonymous").</summary>
    public required string Actor { get; init; }

    /// <summary>Dotted action (e.g. "role.create", "role.update", "role.delete").</summary>
    public required string Action { get; init; }

    /// <summary>Optional role id the change targeted.</summary>
    public string? RoleId { get; init; }

    /// <summary>Outcome: "Success", "Failure", or "Denied".</summary>
    public required string Outcome { get; init; }

    /// <summary>Optional pre-sanitized JSON detail blob.</summary>
    public string? Details { get; init; }
}

/// <summary>A page of role-change audit entries, newest first.</summary>
public sealed record ConsoleRoleAuditPage
{
    /// <summary>The audit entries.</summary>
    public IReadOnlyList<ConsoleRoleAuditEntry> Entries { get; init; } = [];

    /// <summary>Opaque cursor for the next page, or null when there are no more.</summary>
    public string? NextCursor { get; init; }
}
