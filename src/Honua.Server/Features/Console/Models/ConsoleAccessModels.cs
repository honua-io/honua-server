// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Console.Models;

public sealed record ConsoleRoleWriteRequest
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<ConsolePermissionGrant> Grants { get; init; } = [];
}

public sealed record ConsoleAccessScope
{
    public required string Level { get; init; }

    public required string Label { get; init; }

    public string? Description { get; init; }
}

public sealed record ConsoleRbacPermissionColumn
{
    public required string Key { get; init; }

    public required string Label { get; init; }
}

public sealed record ConsolePermissionGrant
{
    public required string Permission { get; init; }

    public required string Grant { get; init; }

    public string? Qualifier { get; init; }
}

public sealed record ConsoleRbacRole
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public bool IsCustom { get; init; }

    public IReadOnlyList<ConsolePermissionGrant> Grants { get; init; } = [];
}

public sealed record ConsoleRbacOverview
{
    public required string WorkspaceId { get; init; }

    public string? WorkspaceName { get; init; }

    public IReadOnlyList<ConsoleAccessScope> Scopes { get; init; } = [];

    public IReadOnlyList<ConsoleRbacPermissionColumn> Permissions { get; init; } = [];

    public IReadOnlyList<ConsoleRbacRole> Roles { get; init; } = [];

    public int BuiltInRoleCount { get; init; }

    public int CustomRoleCount { get; init; }

    public int MembersAffected { get; init; }

    public bool CanManageRoles { get; init; }
}

public sealed record ConsoleTeamMember
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string? Identity { get; init; }

    public string? RoleId { get; init; }

    public required string RoleName { get; init; }

    public bool IsCustomRole { get; init; }

    public string? Scope { get; init; }

    public string? LastActive { get; init; }

    public string? Source { get; init; }

    public string? PrincipalKind { get; init; }
}

public sealed record ConsoleTeamInvitation
{
    public required string Id { get; init; }

    public required string Identity { get; init; }

    public required string RoleName { get; init; }

    public bool IsCustomRole { get; init; }

    public string? Scope { get; init; }

    public string? Status { get; init; }
}

public sealed record ConsoleTeamMembership
{
    public required string WorkspaceId { get; init; }

    public string? WorkspaceName { get; init; }

    public IReadOnlyList<ConsoleTeamMember> Members { get; init; } = [];

    public IReadOnlyList<ConsoleTeamInvitation> Invitations { get; init; } = [];

    public int ActiveCount { get; init; }

    public int PendingCount { get; init; }

    public int DeactivatedCount { get; init; }

    public bool CanInvite { get; init; }
}

public sealed record ConsoleRoleAuditEntry
{
    public long Id { get; init; }

    public string? Timestamp { get; init; }

    public string? Actor { get; init; }

    public string? Action { get; init; }

    public string? RoleId { get; init; }

    public string? Outcome { get; init; }

    public string? Details { get; init; }
}

public sealed record ConsoleRoleAuditPage
{
    public IReadOnlyList<ConsoleRoleAuditEntry> Entries { get; init; } = [];

    public string? NextCursor { get; init; }
}
