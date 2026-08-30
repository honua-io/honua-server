// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Console.Models;

/// <summary>Request body for creating or updating a Console custom role.</summary>
public sealed record ConsoleRoleWriteRequest
{
    /// <summary>Human-readable role name.</summary>
    public required string Name { get; init; }

    /// <summary>Optional role description.</summary>
    public string? Description { get; init; }

    /// <summary>Console permission grants assigned to the role.</summary>
    public IReadOnlyList<ConsolePermissionGrant> Grants { get; init; } = [];
}

/// <summary>One level in the Console access-scope hierarchy.</summary>
public sealed record ConsoleAccessScope
{
    /// <summary>Stable scope-level key.</summary>
    public required string Level { get; init; }

    /// <summary>Human-readable scope label.</summary>
    public required string Label { get; init; }

    /// <summary>Optional scope description.</summary>
    public string? Description { get; init; }
}

/// <summary>One permission column in the Console role matrix.</summary>
public sealed record ConsoleRbacPermissionColumn
{
    /// <summary>Stable permission key.</summary>
    public required string Key { get; init; }

    /// <summary>Human-readable permission label.</summary>
    public required string Label { get; init; }
}

/// <summary>A role's grant for one Console permission.</summary>
public sealed record ConsolePermissionGrant
{
    /// <summary>Stable permission key.</summary>
    public required string Permission { get; init; }

    /// <summary>Closed grant state, currently <c>granted</c> or <c>not-granted</c> for writes.</summary>
    public required string Grant { get; init; }

    /// <summary>Optional human-readable qualifier.</summary>
    public string? Qualifier { get; init; }
}

/// <summary>A built-in or custom role projected for the Console Access surface.</summary>
public sealed record ConsoleRbacRole
{
    /// <summary>Stable role identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Role name.</summary>
    public required string Name { get; init; }

    /// <summary>Optional role description.</summary>
    public string? Description { get; init; }

    /// <summary>Whether operators may update or delete the role.</summary>
    public bool IsCustom { get; init; }

    /// <summary>Role grants across the fixed Console permission columns.</summary>
    public IReadOnlyList<ConsolePermissionGrant> Grants { get; init; } = [];
}

/// <summary>Workspace-scoped Console role and permission overview.</summary>
public sealed record ConsoleRbacOverview
{
    /// <summary>Requested workspace identifier.</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>Human-readable workspace name when known.</summary>
    public string? WorkspaceName { get; init; }

    /// <summary>Supported access-scope hierarchy.</summary>
    public IReadOnlyList<ConsoleAccessScope> Scopes { get; init; } = [];

    /// <summary>Fixed permission columns.</summary>
    public IReadOnlyList<ConsoleRbacPermissionColumn> Permissions { get; init; } = [];

    /// <summary>Roles visible in the workspace.</summary>
    public IReadOnlyList<ConsoleRbacRole> Roles { get; init; } = [];

    /// <summary>Number of built-in roles.</summary>
    public int BuiltInRoleCount { get; init; }

    /// <summary>Number of custom roles.</summary>
    public int CustomRoleCount { get; init; }

    /// <summary>Number of managed members assigned at least one visible role.</summary>
    public int MembersAffected { get; init; }

    /// <summary>Whether the authenticated caller may manage roles.</summary>
    public bool CanManageRoles { get; init; }
}

/// <summary>One managed identity in the Console membership roster.</summary>
public sealed record ConsoleTeamMember
{
    /// <summary>Stable managed-user identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Email or external identity label.</summary>
    public string? Identity { get; init; }

    /// <summary>Primary role identifier when the role is known.</summary>
    public string? RoleId { get; init; }

    /// <summary>Comma-separated assigned role names.</summary>
    public required string RoleName { get; init; }

    /// <summary>Whether the primary role is custom.</summary>
    public bool IsCustomRole { get; init; }

    /// <summary>Scope label for the membership.</summary>
    public string? Scope { get; init; }

    /// <summary>Most recent managed-user update timestamp.</summary>
    public string? LastActive { get; init; }

    /// <summary>Identity provisioning source.</summary>
    public string? Source { get; init; }

    /// <summary>Principal kind.</summary>
    public string? PrincipalKind { get; init; }
}

/// <summary>A pending Console workspace invitation.</summary>
public sealed record ConsoleTeamInvitation
{
    /// <summary>Stable invitation identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Invited identity.</summary>
    public required string Identity { get; init; }

    /// <summary>Assigned role name.</summary>
    public required string RoleName { get; init; }

    /// <summary>Whether the assigned role is custom.</summary>
    public bool IsCustomRole { get; init; }

    /// <summary>Invitation scope.</summary>
    public string? Scope { get; init; }

    /// <summary>Invitation state.</summary>
    public string? Status { get; init; }
}

/// <summary>Workspace-scoped Console membership projection.</summary>
public sealed record ConsoleTeamMembership
{
    /// <summary>Requested workspace identifier.</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>Human-readable workspace name when known.</summary>
    public string? WorkspaceName { get; init; }

    /// <summary>Managed member rows.</summary>
    public IReadOnlyList<ConsoleTeamMember> Members { get; init; } = [];

    /// <summary>Pending invitation rows. Empty until an invitation store is configured.</summary>
    public IReadOnlyList<ConsoleTeamInvitation> Invitations { get; init; } = [];

    /// <summary>Number of active managed users.</summary>
    public int ActiveCount { get; init; }

    /// <summary>Number of pending invitations.</summary>
    public int PendingCount { get; init; }

    /// <summary>Number of deactivated managed users.</summary>
    public int DeactivatedCount { get; init; }

    /// <summary>Whether the authenticated caller may invite members.</summary>
    public bool CanInvite { get; init; }
}

/// <summary>One Console role mutation audit record.</summary>
public sealed record ConsoleRoleAuditEntry
{
    /// <summary>Audit sink row identifier.</summary>
    public long Id { get; init; }

    /// <summary>UTC event timestamp.</summary>
    public string? Timestamp { get; init; }

    /// <summary>Actor identifier.</summary>
    public string? Actor { get; init; }

    /// <summary>Stable dotted action.</summary>
    public string? Action { get; init; }

    /// <summary>Affected role identifier.</summary>
    public string? RoleId { get; init; }

    /// <summary>Audit outcome.</summary>
    public string? Outcome { get; init; }

    /// <summary>Sanitized audit detail.</summary>
    public string? Details { get; init; }
}

/// <summary>Cursor-paginated Console role audit records.</summary>
public sealed record ConsoleRoleAuditPage
{
    /// <summary>Role audit entries ordered newest-first.</summary>
    public IReadOnlyList<ConsoleRoleAuditEntry> Entries { get; init; } = [];

    /// <summary>Opaque cursor for the next page.</summary>
    public string? NextCursor { get; init; }
}
