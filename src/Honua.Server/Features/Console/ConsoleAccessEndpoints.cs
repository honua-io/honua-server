// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Console.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Console;

internal static class ConsoleAccessEndpoints
{
    private const string ConsoleAccessService = "console-access";
    private const string EnvironmentMembershipOperation = "environment-membership";
    private const string Granted = "granted";
    private const string NotGranted = "not-granted";
    private const int MaxEnvironmentIdLength = 64;
    private const int MaxRoleNameLength = 100;
    private const int MaxRoleDescriptionLength = 500;
    private const int UserPageSize = 1_000;

    private static readonly ConsoleAccessScope[] Scopes =
    [
        new() { Level = "environment", Label = "Environment", Description = "Environment-wide role assignment." },
        new() { Level = "content", Label = "Content", Description = "Map, dashboard, report, and other content." },
        new() { Level = "layer", Label = "Layer", Description = "Dataset exposure through a Service." },
        new() { Level = "dataset-field", Label = "Dataset field", Description = "Field-level policy override." },
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

    public static void MapConsoleAccessEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/console/access/{environmentId}")
            .WithMetadata(TenantIndependentControlPlaneMetadata.Instance)
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Console", "Access")
            .RequireAdminAuthorization()
            .RequirePlatformAdminAuthority();

        group.MapGet("/roles", HandleGetRoles);
        group.MapGet("/members", HandleGetMembers);
        group.MapPost("/roles", HandleCreateRole)
            .AddAgentApprovalFilter(AgentApprovalFilter.AccessGrants)
            .RequireCredentialManagementAuthority();
        group.MapPut("/roles/{roleId:guid}", HandleUpdateRole)
            .AddAgentApprovalFilter(AgentApprovalFilter.AccessGrants)
            .RequireCredentialManagementAuthority();
        group.MapDelete("/roles/{roleId:guid}", HandleDeleteRole)
            .AddAgentApprovalFilter(AgentApprovalFilter.AccessGrants)
            .RequireCredentialManagementAuthority();
        group.MapGet("/roles/audit", HandleGetRoleAudit);
    }

    private static async Task<IResult> HandleGetRoles(
        string environmentId,
        [FromServices] IRoleStore roleStore,
        [FromServices] IUserStore userStore,
        HttpContext context)
    {
        if (!TryNormalizeEnvironmentId(environmentId, out var normalizedEnvironmentId, out var error))
        {
            return BadRequest(error);
        }

        var allRoles = await roleStore.ListRolesAsync(context.RequestAborted).ConfigureAwait(false);
        var environmentRoleNames = GetEnvironmentRoleNames(allRoles, normalizedEnvironmentId);
        var roles = allRoles
            .Where(role => role.IsBuiltIn || IsOwnedByEnvironment(role, normalizedEnvironmentId))
            .ToArray();
        var users = await ListEnvironmentUsersAsync(userStore, environmentRoleNames, context.RequestAborted).ConfigureAwait(false);
        var projected = roles
            .OrderByDescending(static role => role.IsBuiltIn)
            .ThenBy(static role => role.Name, StringComparer.OrdinalIgnoreCase)
            .Select(role => ToConsoleRole(role, normalizedEnvironmentId))
            .ToArray();

        var overview = new ConsoleRbacOverview
        {
            EnvironmentId = normalizedEnvironmentId,
            EnvironmentName = normalizedEnvironmentId,
            Scopes = Scopes,
            Permissions = Permissions,
            Roles = projected,
            BuiltInRoleCount = roles.Count(static role => role.IsBuiltIn),
            CustomRoleCount = roles.Count(static role => !role.IsBuiltIn),
            MembersAffected = users.Count(user => user.IsActive && user.Roles.Any(environmentRoleNames.Contains)),
            CanManageRoles = true,
        };

        return Results.Ok(ApiResponse<ConsoleRbacOverview>.CreateSuccess(overview));
    }

    private static async Task<IResult> HandleGetMembers(
        string environmentId,
        [FromServices] IRoleStore roleStore,
        [FromServices] IUserStore userStore,
        HttpContext context)
    {
        if (!TryNormalizeEnvironmentId(environmentId, out var normalizedEnvironmentId, out var error))
        {
            return BadRequest(error);
        }

        var allRoles = await roleStore.ListRolesAsync(context.RequestAborted).ConfigureAwait(false);
        var environmentRoleNames = GetEnvironmentRoleNames(allRoles, normalizedEnvironmentId);
        var roles = allRoles
            .Where(role => role.IsBuiltIn || IsOwnedByEnvironment(role, normalizedEnvironmentId))
            .ToArray();
        var rolesByName = roles.ToDictionary(static role => role.Name, StringComparer.OrdinalIgnoreCase);
        var users = await ListEnvironmentUsersAsync(userStore, environmentRoleNames, context.RequestAborted).ConfigureAwait(false);
        var members = users
            .OrderBy(static user => user.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(user => ToTeamMember(user, rolesByName, normalizedEnvironmentId))
            .ToArray();

        var membership = new ConsoleTeamMembership
        {
            EnvironmentId = normalizedEnvironmentId,
            EnvironmentName = normalizedEnvironmentId,
            Members = members,
            Invitations = [],
            ActiveCount = users.Count(static user => user.IsActive),
            PendingCount = 0,
            DeactivatedCount = users.Count(static user => !user.IsActive),
            // Invitations are not mutable until an environment-scoped invitation store and
            // endpoint exist. Do not advertise a capability the server cannot honor.
            CanInvite = false,
        };

        return Results.Ok(ApiResponse<ConsoleTeamMembership>.CreateSuccess(membership));
    }

    private static async Task<IResult> HandleCreateRole(
        string environmentId,
        ConsoleRoleWriteRequest request,
        [FromServices] IRoleStore roleStore,
        [FromServices] IAuditLog auditLog,
        HttpContext context)
    {
        if (!TryValidateWrite(environmentId, request, out var normalizedEnvironmentId, out var grants, out var error))
        {
            return BadRequest(error);
        }

        var requestedName = request.Name.Trim();
        var allRoles = await roleStore.ListRolesAsync(context.RequestAborted).ConfigureAwait(false);
        if (HasRoleNameCollision(allRoles, requestedName))
        {
            return RoleNameConflict(requestedName);
        }

        var role = new RoleDefinition
        {
            Name = requestedName,
            Description = NormalizeDescription(request.Description),
            Permissions = grants,
        };
        RoleDefinition created;
        try
        {
            created = await roleStore.CreateRoleAsync(role, context.RequestAborted).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The durable store enforces the same global name uniqueness. Translate a
            // race between the preflight and insert into the public conflict contract.
            return RoleNameConflict(requestedName);
        }

        await RecordRoleAuditAsync(
            auditLog, context, normalizedEnvironmentId, created.RoleId, "console_access.role.create")
            .ConfigureAwait(false);

        return Results.Created(
            $"/api/v1/console/access/{Uri.EscapeDataString(normalizedEnvironmentId)}/roles/{created.RoleId:D}",
            ApiResponse<ConsoleRbacRole>.CreateSuccess(ToConsoleRole(created, normalizedEnvironmentId)));
    }

    private static async Task<IResult> HandleUpdateRole(
        string environmentId,
        Guid roleId,
        ConsoleRoleWriteRequest request,
        [FromServices] IRoleStore roleStore,
        [FromServices] IAuditLog auditLog,
        HttpContext context)
    {
        if (!TryValidateWrite(environmentId, request, out var normalizedEnvironmentId, out var grants, out var error))
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

        if (!IsOwnedByEnvironment(existing, normalizedEnvironmentId))
        {
            return Results.NotFound(ApiResponse<object>.Failure("Role not found."));
        }

        var requestedName = request.Name.Trim();
        if (!string.Equals(requestedName, existing.Name, StringComparison.Ordinal))
        {
            return BadRequest("Role names cannot be changed after creation.");
        }

        var allRoles = await roleStore.ListRolesAsync(context.RequestAborted).ConfigureAwait(false);
        if (HasRoleNameCollision(allRoles, requestedName, existing.RoleId))
        {
            return RoleNameConflict(requestedName);
        }

        RoleDefinition? updated;
        try
        {
            updated = await roleStore.UpdateRoleAsync(new RoleDefinition
            {
                RoleId = existing.RoleId,
                Name = requestedName,
                Description = NormalizeDescription(request.Description),
                IsBuiltIn = false,
                Permissions = grants,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            }, context.RequestAborted).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return RoleNameConflict(requestedName);
        }

        if (updated is null)
        {
            return Results.NotFound(ApiResponse<object>.Failure("Role not found."));
        }

        await RecordRoleAuditAsync(
            auditLog, context, normalizedEnvironmentId, roleId, "console_access.role.update")
            .ConfigureAwait(false);

        return Results.Ok(ApiResponse<ConsoleRbacRole>.CreateSuccess(ToConsoleRole(updated, normalizedEnvironmentId)));
    }

    private static async Task<IResult> HandleDeleteRole(
        string environmentId,
        Guid roleId,
        [FromServices] IRoleStore roleStore,
        [FromServices] IAuditLog auditLog,
        HttpContext context)
    {
        if (!TryNormalizeEnvironmentId(environmentId, out var normalizedEnvironmentId, out var error))
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

        if (!IsOwnedByEnvironment(existing, normalizedEnvironmentId))
        {
            return Results.NotFound(ApiResponse<object>.Failure("Role not found."));
        }

        if (!await roleStore.DeleteRoleAsync(roleId, context.RequestAborted).ConfigureAwait(false))
        {
            return Results.NotFound(ApiResponse<object>.Failure("Role not found."));
        }

        await RecordRoleAuditAsync(
            auditLog, context, normalizedEnvironmentId, roleId, "console_access.role.delete")
            .ConfigureAwait(false);
        return Results.Ok(ApiResponse<object>.SuccessWithMessage("Role deleted."));
    }

    private static async Task<IResult> HandleGetRoleAudit(
        string environmentId,
        [FromServices] IAuditLogReader auditReader,
        HttpContext context,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? cursor = null)
    {
        if (!TryNormalizeEnvironmentId(environmentId, out var normalizedEnvironmentId, out var error))
        {
            return BadRequest(error);
        }

        if (pageSize is < 1 or > 200)
        {
            return BadRequest("pageSize must be between 1 and 200.");
        }

        AuditEventPage page;
        try
        {
            page = await auditReader.ListAsync(new AuditLogFilter
            {
                ResourceType = AuditResourceType(normalizedEnvironmentId),
                EventTypes = [AuditEventType.Authorization],
                PageSize = pageSize,
                Cursor = cursor,
            }, context.RequestAborted).ConfigureAwait(false);
        }
        catch (InvalidAuditLogCursorException)
        {
            return BadRequest("cursor is malformed.");
        }

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

    private static ConsoleRbacRole ToConsoleRole(RoleDefinition role, string environmentId) => new()
    {
        Id = role.RoleId.ToString("D"),
        Name = role.Name,
        Description = role.Description,
        IsCustom = !role.IsBuiltIn,
        Grants = Permissions.Select(permission => new ConsolePermissionGrant
        {
            Permission = permission.Key,
            Grant = HasPermission(role.Permissions, environmentId, permission.Key) ? Granted : NotGranted,
        }).ToArray(),
    };

    private static ConsoleTeamMember ToTeamMember(
        ManagedUser user,
        Dictionary<string, RoleDefinition> rolesByName,
        string environmentId)
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
            Scope = environmentId,
            LastActive = user.UpdatedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            Source = user.ProvisioningSource,
            PrincipalKind = "user",
        };
    }

    private static bool HasPermission(
        IReadOnlyList<PermissionGrant> grants,
        string environmentId,
        string permissionKey)
    {
        foreach (var grant in grants)
        {
            if (!IsApplicable(grant, environmentId))
            {
                continue;
            }

            var operation = grant.Operation.Trim();
            if (operation is "*" || string.Equals(operation, ToCanonicalOperation(permissionKey), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsApplicable(PermissionGrant grant, string environmentId) =>
        string.Equals(grant.Service, "*", StringComparison.Ordinal)
        || (string.Equals(grant.Service, ConsoleAccessService, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(grant.Layer, "*", StringComparison.Ordinal)
                || string.Equals(grant.Layer, environmentId, StringComparison.Ordinal)));

    private static bool IsOwnedByEnvironment(RoleDefinition role, string environmentId) =>
        role.Permissions.Any(grant =>
            string.Equals(grant.Service, ConsoleAccessService, StringComparison.OrdinalIgnoreCase)
            && string.Equals(grant.Layer, environmentId, StringComparison.Ordinal)
            && string.Equals(grant.Operation, EnvironmentMembershipOperation, StringComparison.Ordinal));

    private static HashSet<string> GetEnvironmentRoleNames(
        IReadOnlyList<RoleDefinition> roles,
        string environmentId) =>
        roles
            .Where(role => !role.IsBuiltIn && IsOwnedByEnvironment(role, environmentId))
            .Select(static role => role.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // A server-assigned role name counts as taken: a stored role under it would hand its grants to
    // every principal the server stamps with that role.
    private static bool HasRoleNameCollision(
        IReadOnlyList<RoleDefinition> roles,
        string requestedName,
        Guid? currentRoleId = null) =>
        ReservedRoleNames.Contains(requestedName) || roles.Any(role =>
            role.RoleId != currentRoleId &&
            string.Equals(role.Name, requestedName, StringComparison.OrdinalIgnoreCase));

    private static bool TryValidateWrite(
        string environmentId,
        ConsoleRoleWriteRequest? request,
        out string normalizedEnvironmentId,
        out IReadOnlyList<PermissionGrant> grants,
        out string error)
    {
        grants = [];
        if (!TryNormalizeEnvironmentId(environmentId, out normalizedEnvironmentId, out error))
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
            // RoleDefinition is provider-neutral and has no environment column. Persist a
            // reserved Console grant so ownership survives even when every UI permission
            // is explicitly not granted.
            new()
            {
                Service = ConsoleAccessService,
                Layer = normalizedEnvironmentId,
                Operation = EnvironmentMembershipOperation,
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
                Layer = normalizedEnvironmentId,
                Operation = ToCanonicalOperation(grant.Permission),
            });
        }

        grants = mapped;
        error = string.Empty;
        return true;
    }

    private static bool TryNormalizeEnvironmentId(
        string environmentId,
        out string normalizedEnvironmentId,
        out string error)
    {
        normalizedEnvironmentId = environmentId?.Trim() ?? string.Empty;
        if (normalizedEnvironmentId.Length is 0 or > MaxEnvironmentIdLength)
        {
            error = $"environmentId must contain between 1 and {MaxEnvironmentIdLength} characters.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static async Task<IReadOnlyList<ManagedUser>> ListEnvironmentUsersAsync(
        IUserStore userStore,
        IReadOnlySet<string> roleNames,
        CancellationToken cancellationToken)
    {
        var users = new Dictionary<string, ManagedUser>(StringComparer.Ordinal);
        foreach (var roleName in roleNames)
        {
            for (var offset = 0; ; offset += UserPageSize)
            {
                var page = await userStore.ListUsersAsync(new UserListFilter
                {
                    Role = roleName,
                    Limit = UserPageSize,
                    Offset = offset,
                }, cancellationToken).ConfigureAwait(false);
                foreach (var user in page.Users)
                {
                    users[user.UserId] = user;
                }

                if (offset + page.Users.Count >= page.TotalCount || page.Users.Count == 0)
                {
                    break;
                }
            }
        }

        return users.Values.ToArray();
    }

    private static Task<string?> RecordRoleAuditAsync(
        IAuditLog auditLog,
        HttpContext context,
        string environmentId,
        Guid roleId,
        string action)
    {
        var actor = ConsolePrincipal.ResolveActorId(context.User) ?? AuditEvent.AnonymousActor;
        return auditLog.RecordAsync(new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.Authorization,
            Actor = actor,
            ActorType = actor == AuditEvent.AnonymousActor ? AuditActorType.Anonymous : AuditActorType.UserId,
            ResourceType = AuditResourceType(environmentId),
            ResourceId = roleId.ToString("D"),
            Action = action,
            Outcome = AuditOutcome.Success,
            CorrelationId = context.TraceIdentifier,
            Details = string.Empty,
        }, CancellationToken.None);
    }

    private static string AuditResourceType(string environmentId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(environmentId)));
        return $"console_access_role:{hash[..32]}";
    }

    private static string ToCanonicalOperation(string permissionKey) => permissionKey switch
    {
        "view-public" => "query",
        "comment" => "write",
        "draft" => "edit",
        "publish" => "publish",
        "manage-roles" => "administer",
        _ => permissionKey,
    };

    private static string? NormalizeDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    private static IResult BadRequest(string message) =>
        Results.BadRequest(ApiResponse<object>.Failure(message));

    private static IResult RoleNameConflict(string roleName) =>
        Results.Conflict(ApiResponse<object>.Failure(
            $"Role name '{roleName}' is already in use. Role names must be unique across environments."));
}
