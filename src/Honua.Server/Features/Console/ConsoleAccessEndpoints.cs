// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Console.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Console;

/// <summary>Console-facing projection over the canonical role, user, and audit stores.</summary>
internal static class ConsoleAccessEndpoints
{
    private const string ConsoleAccessService = "console-access";
    private const string WorkspaceMembershipOperation = "workspace-membership";
    private const string Granted = "granted";
    private const string NotGranted = "not-granted";
    private const int MaxWorkspaceIdLength = 64;
    private const int MaxRoleNameLength = 100;
    private const int MaxRoleDescriptionLength = 500;
    private const int UserPageSize = 1_000;

    private static readonly ConsoleAccessScope[] Scopes =
    [
        new() { Level = "workspace", Label = "Workspace", Description = "Workspace-wide role assignment." },
        new() { Level = "environment", Label = "Environment", Description = "Environment-specific override." },
        new() { Level = "content", Label = "Content", Description = "Map, dashboard, report, and other content." },
        new() { Level = "publication", Label = "Publication", Description = "Published route or share surface." },
        new() { Level = "resource-field", Label = "Resource field", Description = "Field-level policy override." },
    ];

    private static readonly ConsoleRbacPermissionColumn[] Permissions =
    [
        new() { Key = "view-public", Label = "View public" },
        new() { Key = "comment", Label = "Comment" },
        new() { Key = "draft", Label = "Draft / collaborate" },
        new() { Key = "publish", Label = "Publish" },
        new() { Key = "manage-roles", Label = "Manage roles" },
    ];

    private static readonly HashSet<string> PermissionKeys =
        Permissions.Select(static permission => permission.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>Maps the six admin-authorized Console Access routes.</summary>
    public static void MapConsoleAccessEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/console/access/{workspaceId}")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Console", "Access")
            .RequireAdminAuthorization();

        group.MapGet("/roles", HandleGetRoles);
        group.MapGet("/members", HandleGetMembers);
        group.MapPost("/roles", HandleCreateRole);
        group.MapPut("/roles/{roleId:guid}", HandleUpdateRole);
        group.MapDelete("/roles/{roleId:guid}", HandleDeleteRole);
        group.MapGet("/roles/audit", HandleGetRoleAudit);
    }

    private static async Task<IResult> HandleGetRoles(
        string workspaceId,
        [FromServices] IRoleStore roleStore,
        [FromServices] IUserStore userStore,
        HttpContext context)
    {
        if (!TryNormalizeWorkspaceId(workspaceId, out var normalizedWorkspaceId, out var error))
        {
            return BadRequest(error);
        }

        var allRoles = await roleStore.ListRolesAsync(context.RequestAborted).ConfigureAwait(false);
        var workspaceRoleNames = GetWorkspaceRoleNames(allRoles, normalizedWorkspaceId);
        var roles = allRoles
            .Where(role => role.IsBuiltIn || IsOwnedByWorkspace(role, normalizedWorkspaceId))
            .ToArray();
        var users = await ListAllUsersAsync(userStore, context.RequestAborted).ConfigureAwait(false);
        var projected = roles
            .OrderByDescending(static role => role.IsBuiltIn)
            .ThenBy(static role => role.Name, StringComparer.OrdinalIgnoreCase)
            .Select(role => ToConsoleRole(role, normalizedWorkspaceId))
            .ToArray();

        var overview = new ConsoleRbacOverview
        {
            WorkspaceId = normalizedWorkspaceId,
            WorkspaceName = normalizedWorkspaceId,
            Scopes = Scopes,
            Permissions = Permissions,
            Roles = projected,
            BuiltInRoleCount = roles.Count(static role => role.IsBuiltIn),
            CustomRoleCount = roles.Count(static role => !role.IsBuiltIn),
            MembersAffected = users.Count(user => user.IsActive && user.Roles.Any(workspaceRoleNames.Contains)),
            CanManageRoles = true,
        };

        return Results.Ok(ApiResponse<ConsoleRbacOverview>.CreateSuccess(overview));
    }

    private static async Task<IResult> HandleGetMembers(
        string workspaceId,
        [FromServices] IRoleStore roleStore,
        [FromServices] IUserStore userStore,
        HttpContext context)
    {
        if (!TryNormalizeWorkspaceId(workspaceId, out var normalizedWorkspaceId, out var error))
        {
            return BadRequest(error);
        }

        var allRoles = await roleStore.ListRolesAsync(context.RequestAborted).ConfigureAwait(false);
        var workspaceRoleNames = GetWorkspaceRoleNames(allRoles, normalizedWorkspaceId);
        var roles = allRoles
            .Where(role => role.IsBuiltIn || IsOwnedByWorkspace(role, normalizedWorkspaceId))
            .ToArray();
        var rolesByName = roles.ToDictionary(static role => role.Name, StringComparer.OrdinalIgnoreCase);
        var allUsers = await ListAllUsersAsync(userStore, context.RequestAborted).ConfigureAwait(false);
        var users = allUsers
            .Where(user => user.Roles.Any(workspaceRoleNames.Contains))
            .ToArray();
        var members = users
            .OrderBy(static user => user.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(user => ToTeamMember(user, rolesByName, normalizedWorkspaceId))
            .ToArray();

        var membership = new ConsoleTeamMembership
        {
            WorkspaceId = normalizedWorkspaceId,
            WorkspaceName = normalizedWorkspaceId,
            Members = members,
            Invitations = [],
            ActiveCount = users.Count(static user => user.IsActive),
            PendingCount = 0,
            DeactivatedCount = users.Count(static user => !user.IsActive),
            CanInvite = true,
        };

        return Results.Ok(ApiResponse<ConsoleTeamMembership>.CreateSuccess(membership));
    }

    private static async Task<IResult> HandleCreateRole(
        string workspaceId,
        ConsoleRoleWriteRequest request,
        [FromServices] IRoleStore roleStore,
        [FromServices] IAuditLog auditLog,
        HttpContext context)
    {
        if (!TryValidateWrite(workspaceId, request, out var normalizedWorkspaceId, out var grants, out var error))
        {
            return BadRequest(error);
        }

        var role = new RoleDefinition
        {
            Name = request.Name.Trim(),
            Description = NormalizeDescription(request.Description),
            Permissions = grants,
        };
        var created = await roleStore.CreateRoleAsync(role, context.RequestAborted).ConfigureAwait(false);
        _ = await roleStore.SetPermissionsAsync(created.RoleId, grants, context.RequestAborted).ConfigureAwait(false);
        created = await roleStore.GetRoleAsync(created.RoleId, context.RequestAborted).ConfigureAwait(false) ?? created;
        await RecordRoleAuditAsync(
            auditLog, context, normalizedWorkspaceId, created.RoleId, "console_access.role.create")
            .ConfigureAwait(false);

        return Results.Created(
            $"/api/v1/console/access/{Uri.EscapeDataString(normalizedWorkspaceId)}/roles/{created.RoleId:D}",
            ApiResponse<ConsoleRbacRole>.CreateSuccess(ToConsoleRole(created, normalizedWorkspaceId)));
    }

    private static async Task<IResult> HandleUpdateRole(
        string workspaceId,
        Guid roleId,
        ConsoleRoleWriteRequest request,
        [FromServices] IRoleStore roleStore,
        [FromServices] IAuditLog auditLog,
        HttpContext context)
    {
        if (!TryValidateWrite(workspaceId, request, out var normalizedWorkspaceId, out var grants, out var error))
        {
            return BadRequest(error);
        }

        var existing = await roleStore.GetRoleAsync(roleId, context.RequestAborted).ConfigureAwait(false);
        if (existing is null)
        {
            return Results.NotFound(ApiResponse<object>.Failure("Role not found."));
        }

        if (existing.IsBuiltIn)
        {
            return BadRequest("Built-in roles cannot be updated.");
        }

        if (!IsOwnedByWorkspace(existing, normalizedWorkspaceId))
        {
            return Results.NotFound(ApiResponse<object>.Failure("Role not found."));
        }

        var updated = await roleStore.UpdateRoleAsync(new RoleDefinition
        {
            RoleId = existing.RoleId,
            Name = request.Name.Trim(),
            Description = NormalizeDescription(request.Description),
            IsBuiltIn = false,
            Permissions = grants,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, context.RequestAborted).ConfigureAwait(false);
        if (updated is null)
        {
            return Results.NotFound(ApiResponse<object>.Failure("Role not found."));
        }

        _ = await roleStore.SetPermissionsAsync(roleId, grants, context.RequestAborted).ConfigureAwait(false);
        updated = await roleStore.GetRoleAsync(roleId, context.RequestAborted).ConfigureAwait(false) ?? updated;
        await RecordRoleAuditAsync(
            auditLog, context, normalizedWorkspaceId, roleId, "console_access.role.update")
            .ConfigureAwait(false);

        return Results.Ok(ApiResponse<ConsoleRbacRole>.CreateSuccess(ToConsoleRole(updated, normalizedWorkspaceId)));
    }

    private static async Task<IResult> HandleDeleteRole(
        string workspaceId,
        Guid roleId,
        [FromServices] IRoleStore roleStore,
        [FromServices] IAuditLog auditLog,
        HttpContext context)
    {
        if (!TryNormalizeWorkspaceId(workspaceId, out var normalizedWorkspaceId, out var error))
        {
            return BadRequest(error);
        }

        var existing = await roleStore.GetRoleAsync(roleId, context.RequestAborted).ConfigureAwait(false);
        if (existing is null)
        {
            return Results.NotFound(ApiResponse<object>.Failure("Role not found."));
        }

        if (existing.IsBuiltIn)
        {
            return BadRequest("Built-in roles cannot be deleted.");
        }

        if (!IsOwnedByWorkspace(existing, normalizedWorkspaceId))
        {
            return Results.NotFound(ApiResponse<object>.Failure("Role not found."));
        }

        if (!await roleStore.DeleteRoleAsync(roleId, context.RequestAborted).ConfigureAwait(false))
        {
            return Results.NotFound(ApiResponse<object>.Failure("Role not found."));
        }

        await RecordRoleAuditAsync(
            auditLog, context, normalizedWorkspaceId, roleId, "console_access.role.delete")
            .ConfigureAwait(false);
        return Results.Ok(ApiResponse<object>.SuccessWithMessage("Role deleted."));
    }

    private static async Task<IResult> HandleGetRoleAudit(
        string workspaceId,
        [FromServices] IAuditLogReader auditReader,
        HttpContext context,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? cursor = null)
    {
        if (!TryNormalizeWorkspaceId(workspaceId, out var normalizedWorkspaceId, out var error))
        {
            return BadRequest(error);
        }

        if (pageSize is < 1 or > 200)
        {
            return BadRequest("pageSize must be between 1 and 200.");
        }

        var page = await auditReader.ListAsync(new AuditLogFilter
        {
            ResourceType = AuditResourceType(normalizedWorkspaceId),
            EventTypes = [AuditEventType.Authorization],
            PageSize = pageSize,
            Cursor = cursor,
        }, context.RequestAborted).ConfigureAwait(false);
        var result = new ConsoleRoleAuditPage
        {
            Entries = page.Items.Select(static item => new ConsoleRoleAuditEntry
            {
                Id = item.AuditId,
                Timestamp = item.Timestamp.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                Actor = item.Actor,
                Action = item.Action,
                RoleId = item.ResourceId,
                Outcome = item.Outcome.ToString().ToLowerInvariant(),
                Details = item.Details,
            }).ToArray(),
            NextCursor = page.NextCursor,
        };

        return Results.Ok(ApiResponse<ConsoleRoleAuditPage>.CreateSuccess(result));
    }

    private static ConsoleRbacRole ToConsoleRole(RoleDefinition role, string workspaceId) => new()
    {
        Id = role.RoleId.ToString("D"),
        Name = role.Name,
        Description = role.Description,
        IsCustom = !role.IsBuiltIn,
        Grants = Permissions.Select(permission => new ConsolePermissionGrant
        {
            Permission = permission.Key,
            Grant = HasPermission(role.Permissions, workspaceId, permission.Key) ? Granted : NotGranted,
        }).ToArray(),
    };

    private static ConsoleTeamMember ToTeamMember(
        ManagedUser user,
        Dictionary<string, RoleDefinition> rolesByName,
        string workspaceId)
    {
        var assignedRoles = user.Roles
            .Select(roleName => rolesByName.TryGetValue(roleName, out var role) ? role : null)
            .Where(static role => role is not null)
            .Select(static role => role!)
            .ToArray();
        var primaryRole = assignedRoles.FirstOrDefault();
        return new ConsoleTeamMember
        {
            Id = user.UserId,
            DisplayName = user.DisplayName,
            Identity = user.Email ?? user.ExternalId,
            RoleId = primaryRole?.RoleId.ToString("D"),
            RoleName = string.Join(", ", assignedRoles.Select(static role => role.Name)),
            IsCustomRole = primaryRole is { IsBuiltIn: false },
            Scope = workspaceId,
            LastActive = user.UpdatedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            Source = user.ProvisioningSource,
            PrincipalKind = "user",
        };
    }

    private static bool HasPermission(
        IReadOnlyList<PermissionGrant> grants,
        string workspaceId,
        string permissionKey)
    {
        foreach (var grant in grants)
        {
            if (!IsApplicable(grant, workspaceId))
            {
                continue;
            }

            var operation = grant.Operation.Trim();
            if (operation is "*" || string.Equals(operation, permissionKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (permissionKey == "view-public" && operation is "read" or "query")
            {
                return true;
            }

            if (permissionKey == "draft" && operation is "write" or "create" or "update" or "edit")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsApplicable(PermissionGrant grant, string workspaceId) =>
        string.Equals(grant.Service, "*", StringComparison.Ordinal)
        || (string.Equals(grant.Service, ConsoleAccessService, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(grant.Layer, "*", StringComparison.Ordinal)
                || string.Equals(grant.Layer, workspaceId, StringComparison.Ordinal)));

    private static bool IsOwnedByWorkspace(RoleDefinition role, string workspaceId) =>
        role.Permissions.Any(grant =>
            string.Equals(grant.Service, ConsoleAccessService, StringComparison.OrdinalIgnoreCase)
            && string.Equals(grant.Layer, workspaceId, StringComparison.Ordinal)
            && string.Equals(grant.Operation, WorkspaceMembershipOperation, StringComparison.Ordinal));

    private static HashSet<string> GetWorkspaceRoleNames(
        IReadOnlyList<RoleDefinition> roles,
        string workspaceId) =>
        roles
            .Where(role => !role.IsBuiltIn && IsOwnedByWorkspace(role, workspaceId))
            .Select(static role => role.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool TryValidateWrite(
        string workspaceId,
        ConsoleRoleWriteRequest? request,
        out string normalizedWorkspaceId,
        out IReadOnlyList<PermissionGrant> grants,
        out string error)
    {
        grants = [];
        if (!TryNormalizeWorkspaceId(workspaceId, out normalizedWorkspaceId, out error))
        {
            return false;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Name)
            || request.Name.Trim().Length > MaxRoleNameLength)
        {
            error = $"name must contain between 1 and {MaxRoleNameLength} characters.";
            return false;
        }

        if (request.Description?.Length > MaxRoleDescriptionLength)
        {
            error = $"description must be at most {MaxRoleDescriptionLength} characters.";
            return false;
        }

        var mapped = new List<PermissionGrant>
        {
            // RoleDefinition is provider-neutral and has no workspace column. Persist a
            // reserved Console grant so ownership survives even when every UI permission
            // is explicitly not granted.
            new()
            {
                Service = ConsoleAccessService,
                Layer = normalizedWorkspaceId,
                Operation = WorkspaceMembershipOperation,
            },
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in request.Grants ?? [])
        {
            if (string.IsNullOrWhiteSpace(grant.Permission) || !PermissionKeys.Contains(grant.Permission)
                || !seen.Add(grant.Permission))
            {
                error = "grants must contain unique, supported permission keys.";
                return false;
            }

            if (string.Equals(grant.Grant, NotGranted, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(grant.Grant, Granted, StringComparison.Ordinal) || grant.Qualifier is not null)
            {
                error = "role writes currently support only granted or not-granted permission states without qualifiers.";
                return false;
            }

            mapped.Add(new PermissionGrant
            {
                Service = ConsoleAccessService,
                Layer = normalizedWorkspaceId,
                Operation = grant.Permission,
            });
        }

        grants = mapped;
        error = string.Empty;
        return true;
    }

    private static bool TryNormalizeWorkspaceId(
        string workspaceId,
        out string normalizedWorkspaceId,
        out string error)
    {
        normalizedWorkspaceId = workspaceId?.Trim() ?? string.Empty;
        if (normalizedWorkspaceId.Length is 0 or > MaxWorkspaceIdLength)
        {
            error = $"workspaceId must contain between 1 and {MaxWorkspaceIdLength} characters.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static async Task<IReadOnlyList<ManagedUser>> ListAllUsersAsync(
        IUserStore userStore,
        CancellationToken cancellationToken)
    {
        var users = new List<ManagedUser>();
        for (var offset = 0; ; offset += UserPageSize)
        {
            var page = await userStore.ListUsersAsync(new UserListFilter
            {
                Limit = UserPageSize,
                Offset = offset,
            }, cancellationToken).ConfigureAwait(false);
            users.AddRange(page.Users);
            if (users.Count >= page.TotalCount || page.Users.Count == 0)
            {
                return users;
            }
        }
    }

    private static Task RecordRoleAuditAsync(
        IAuditLog auditLog,
        HttpContext context,
        string workspaceId,
        Guid roleId,
        string action)
    {
        var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.Identity?.Name
            ?? AuditEvent.AnonymousActor;
        return auditLog.RecordAsync(new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.Authorization,
            Actor = actor,
            ActorType = actor == AuditEvent.AnonymousActor ? AuditActorType.Anonymous : AuditActorType.UserId,
            ResourceType = AuditResourceType(workspaceId),
            ResourceId = roleId.ToString("D"),
            Action = action,
            Outcome = AuditOutcome.Success,
            CorrelationId = context.TraceIdentifier,
            Details = string.Empty,
        }, context.RequestAborted);
    }

    private static string AuditResourceType(string workspaceId) => $"console_access_role:{workspaceId}";

    private static string? NormalizeDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    private static IResult BadRequest(string message) =>
        Results.BadRequest(ApiResponse<object>.Failure(message));
}
