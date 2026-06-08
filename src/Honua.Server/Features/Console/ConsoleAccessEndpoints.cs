// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Server.Features.Console.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Console;

/// <summary>
/// Console Access (RBAC) workspace-scoped read API consumed by the console Settings &gt; Access surface.
/// Projects the GLOBAL admin RBAC store (<see cref="IRoleStore"/>) into the workspace-scoped console Access
/// shapes the console deserializes (see Honua.Console.Contracts/RbacAccessShims.cs, honua-server#1162):
/// <list type="bullet">
///   <item><c>GET /api/v1/console/access/{workspaceId}/roles</c> — role x permission overview.</item>
///   <item><c>GET /api/v1/console/access/{workspaceId}/members</c> — membership roster + invitations.</item>
/// </list>
/// MAPPING: this build has no first-class workspace-scoped RBAC model (#1162's ConsoleAccessScope hierarchy /
/// per-workspace memberships / invitations are absent). Global role definitions are surfaced for the workspace
/// and their service/layer/operation grants are folded into the fixed console permission columns. Membership is
/// not tracked server-side, so the members read returns an honest empty-but-valid roster (200, zero counts).
/// Both reads return empty-but-valid 200s for any workspace id (never 404 for an empty-but-valid workspace).
/// </summary>
internal static class ConsoleAccessEndpoints
{
    /// <summary>Log category for Console Access endpoints.</summary>
    internal sealed class ConsoleAccessEndpointsLog;

    // Fixed console permission columns. Global service/layer/operation grants are folded into these.
    private static readonly ConsoleRbacPermission[] PermissionColumns =
    [
        new() { Key = "manage-content", Label = "Manage content" },
        new() { Key = "manage-roles", Label = "Manage roles" },
        new() { Key = "share", Label = "Share" },
        new() { Key = "publish", Label = "Publish" },
        new() { Key = "view", Label = "View" },
    ];

    // Static, server-owned scope vocabulary (workspace > environment > content > publication > resource-field).
    private static readonly ConsoleAccessScope[] ScopeHierarchy =
    [
        new() { Level = "workspace", Label = "Workspace", Description = "The workspace and everything in it." },
        new() { Level = "environment", Label = "Environment", Description = "A deployment environment within the workspace." },
        new() { Level = "content", Label = "Content", Description = "An individual content item." },
        new() { Level = "publication", Label = "Publication", Description = "A published surface of a content item." },
        new() { Level = "resource-field", Label = "Resource field", Description = "A field within a published resource." },
    ];

    /// <summary>Maps the Console Access (RBAC) workspace-scoped read endpoints.</summary>
    public static void MapConsoleAccessEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/console/access")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Console")
            .RequireAdminAuthorization();

        group.MapGet("/{workspaceId}/roles", HandleGetRoles)
            .WithDisplayName("Get Console Access Roles")
            .WithSummary("Returns the workspace's Console Access (RBAC) roles overview: the scope hierarchy strip, the permission columns, the role rows with their grants, and aggregate role/member counts. Projected from the global admin RBAC roles.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/{workspaceId}/members", HandleGetMembers)
            .WithDisplayName("Get Console Access Members")
            .WithSummary("Returns the workspace's Console Access (RBAC) membership roster plus pending invitations and active/pending/deactivated counts. Returns an honest empty roster until workspace membership is tracked server-side.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/{workspaceId}/roles/audit", HandleRoleAudit)
            .WithDisplayName("Get Console Access Role Audit")
            .WithSummary("Returns the role-change audit trail (create/update/delete) for the workspace, newest first, projected from the server audit log (resourceType 'role'). Supports ?pageSize and ?cursor.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/{workspaceId}/roles", HandleCreateRole)
            .WithDisplayName("Create Console Access Role")
            .WithSummary("Creates a custom role from the console permission columns. Records a 'role.create' audit event.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPut("/{workspaceId}/roles/{roleId}", HandleUpdateRole)
            .WithDisplayName("Update Console Access Role")
            .WithSummary("Updates a custom role's name, description, and grants. Built-in roles cannot be edited. Records a 'role.update' audit event.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapDelete("/{workspaceId}/roles/{roleId}", HandleDeleteRole)
            .WithDisplayName("Delete Console Access Role")
            .WithSummary("Deletes a custom role. Built-in roles cannot be deleted. Records a 'role.delete' audit event.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleRbacOverview>>, ProblemHttpResult>> HandleGetRoles(
        string workspaceId,
        [FromServices] IRoleStore store,
        [FromServices] ILogger<ConsoleAccessEndpointsLog> logger,
        HttpContext context)
    {
        try
        {
            var roles = await store.ListRolesAsync(context.RequestAborted).ConfigureAwait(false);

            var projected = roles.Select(ProjectRole).ToArray();
            var overview = new ConsoleRbacOverview
            {
                WorkspaceId = workspaceId,
                WorkspaceName = null,
                Scopes = ScopeHierarchy,
                Permissions = PermissionColumns,
                Roles = projected,
                BuiltInRoleCount = roles.Count(r => r.IsBuiltIn),
                CustomRoleCount = roles.Count(r => !r.IsBuiltIn),
                // Membership is not tracked server-side in this build; surface 0 affected members honestly.
                MembersAffected = 0,
                CanManageRoles = true,
            };

            return TypedResults.Ok(ApiResponse<ConsoleRbacOverview>.CreateSuccess(overview));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "console.access.roles", ex);
            return TypedResults.Problem(
                title: "Console Access roles lookup failed",
                detail: "An internal error occurred while reading the Console Access roles overview.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static Task<Results<Ok<ApiResponse<ConsoleTeamMembership>>, ProblemHttpResult>> HandleGetMembers(
        string workspaceId,
        [FromServices] ILogger<ConsoleAccessEndpointsLog> logger,
        HttpContext context)
    {
        try
        {
            // No server-side workspace-membership roster exists in this build (#1162 model absent), so the
            // honest answer to "who belongs to this workspace?" is an empty-but-valid roster with zero counts,
            // never fabricated members and never a 404 for an empty-but-valid workspace.
            var membership = new ConsoleTeamMembership
            {
                WorkspaceId = workspaceId,
                WorkspaceName = null,
                Members = [],
                Invitations = [],
                ActiveCount = 0,
                PendingCount = 0,
                DeactivatedCount = 0,
                CanInvite = true,
            };

            Results<Ok<ApiResponse<ConsoleTeamMembership>>, ProblemHttpResult> result =
                TypedResults.Ok(ApiResponse<ConsoleTeamMembership>.CreateSuccess(membership));
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "console.access.members", ex);
            Results<Ok<ApiResponse<ConsoleTeamMembership>>, ProblemHttpResult> result = TypedResults.Problem(
                title: "Console Access members lookup failed",
                detail: "An internal error occurred while reading the Console Access membership roster.",
                statusCode: StatusCodes.Status500InternalServerError);
            return Task.FromResult(result);
        }
    }

    private static async Task<Results<Created<ApiResponse<ConsoleRbacRole>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>> HandleCreateRole(
        string workspaceId,
        ConsoleRoleWriteRequest request,
        [FromServices] IRoleStore store,
        [FromServices] IAuditLog auditLog,
        [FromServices] ILogger<ConsoleAccessEndpointsLog> logger,
        HttpContext context)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure("Role name is required."));
        }

        try
        {
            var role = new RoleDefinition
            {
                Name = request.Name.Trim(),
                Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
                IsBuiltIn = false,
                Permissions = ToPermissionGrants(request.Grants),
            };

            var created = await store.CreateRoleAsync(role, context.RequestAborted).ConfigureAwait(false);
            await RecordRoleAuditAsync(auditLog, context, "role.create", created.RoleId.ToString(), created.Name).ConfigureAwait(false);
            return TypedResults.Created(
                $"/api/v1/console/access/{workspaceId}/roles/{created.RoleId}",
                ApiResponse<ConsoleRbacRole>.CreateSuccess(ProjectRole(created)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "console.access.roles.create", ex);
            return TypedResults.Problem(
                title: "Console Access role create failed",
                detail: "An internal error occurred while creating the role.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleRbacRole>>, BadRequest<ApiResponse<object>>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleUpdateRole(
        string workspaceId,
        string roleId,
        ConsoleRoleWriteRequest request,
        [FromServices] IRoleStore store,
        [FromServices] IAuditLog auditLog,
        [FromServices] ILogger<ConsoleAccessEndpointsLog> logger,
        HttpContext context)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure("Role name is required."));
        }

        if (!Guid.TryParse(roleId, out var roleGuid))
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure($"Role id '{roleId}' is not a valid id."));
        }

        try
        {
            var existing = await store.GetRoleAsync(roleGuid, context.RequestAborted).ConfigureAwait(false);
            if (existing is null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure($"Role '{roleId}' was not found."));
            }

            if (existing.IsBuiltIn)
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure("Built-in roles cannot be edited."));
            }

            var updated = new RoleDefinition
            {
                RoleId = roleGuid,
                Name = request.Name.Trim(),
                Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
                IsBuiltIn = false,
                Permissions = ToPermissionGrants(request.Grants),
            };

            var result = await store.UpdateRoleAsync(updated, context.RequestAborted).ConfigureAwait(false);
            if (result is null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure($"Role '{roleId}' was not found."));
            }

            await RecordRoleAuditAsync(auditLog, context, "role.update", result.RoleId.ToString(), result.Name).ConfigureAwait(false);
            return TypedResults.Ok(ApiResponse<ConsoleRbacRole>.CreateSuccess(ProjectRole(result)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "console.access.roles.update", ex);
            return TypedResults.Problem(
                title: "Console Access role update failed",
                detail: "An internal error occurred while updating the role.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<object>>, BadRequest<ApiResponse<object>>, NotFound<ApiResponse<object>>, ProblemHttpResult>> HandleDeleteRole(
        string workspaceId,
        string roleId,
        [FromServices] IRoleStore store,
        [FromServices] IAuditLog auditLog,
        [FromServices] ILogger<ConsoleAccessEndpointsLog> logger,
        HttpContext context)
    {
        if (!Guid.TryParse(roleId, out var roleGuid))
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure($"Role id '{roleId}' is not a valid id."));
        }

        try
        {
            var existing = await store.GetRoleAsync(roleGuid, context.RequestAborted).ConfigureAwait(false);
            if (existing is null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure($"Role '{roleId}' was not found."));
            }

            if (existing.IsBuiltIn)
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure("Built-in roles cannot be deleted."));
            }

            var deleted = await store.DeleteRoleAsync(roleGuid, context.RequestAborted).ConfigureAwait(false);
            if (!deleted)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure($"Role '{roleId}' was not found."));
            }

            await RecordRoleAuditAsync(auditLog, context, "role.delete", roleGuid.ToString(), existing.Name).ConfigureAwait(false);
            return TypedResults.Ok(ApiResponse<object>.CreateSuccess(new object()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "console.access.roles.delete", ex);
            return TypedResults.Problem(
                title: "Console Access role delete failed",
                detail: "An internal error occurred while deleting the role.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<ConsoleRoleAuditPage>>, ProblemHttpResult>> HandleRoleAudit(
        string workspaceId,
        [FromServices] IAuditLogReader reader,
        [FromServices] ILogger<ConsoleAccessEndpointsLog> logger,
        HttpContext context)
    {
        try
        {
            var pageSize = 50;
            if (context.Request.Query.TryGetValue("pageSize", out var pageSizeRaw)
                && int.TryParse(pageSizeRaw, out var parsed) && parsed is > 0 and <= 500)
            {
                pageSize = parsed;
            }

            var cursor = context.Request.Query.TryGetValue("cursor", out var cursorRaw)
                ? cursorRaw.ToString()
                : null;

            // Role changes are recorded with resourceType "role"; project them into the console audit shape.
            var filter = new AuditLogFilter
            {
                ResourceType = "role",
                PageSize = pageSize,
                Cursor = string.IsNullOrWhiteSpace(cursor) ? null : cursor,
            };

            var page = await reader.ListAsync(filter, context.RequestAborted).ConfigureAwait(false);
            var projected = new ConsoleRoleAuditPage
            {
                Entries = page.Items.Select(item => new ConsoleRoleAuditEntry
                {
                    Id = item.AuditId,
                    Timestamp = item.Timestamp.ToString("o"),
                    Actor = item.Actor,
                    Action = item.Action,
                    RoleId = item.ResourceId,
                    Outcome = item.Outcome.ToString(),
                    Details = string.IsNullOrWhiteSpace(item.Details) ? null : item.Details,
                }).ToArray(),
                NextCursor = page.NextCursor,
            };

            return TypedResults.Ok(ApiResponse<ConsoleRoleAuditPage>.CreateSuccess(projected));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ConsoleEndpointsLog.EndpointFailed(logger, "console.access.roles.audit", ex);
            return TypedResults.Problem(
                title: "Console Access role audit lookup failed",
                detail: "An internal error occurred while reading the role audit trail.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Folds the console permission columns the request carries back into wildcard service/layer admin grants.
    /// Each grant that is not "not-granted" maps its column to the corresponding admin operation at full scope.
    /// </summary>
    private static List<PermissionGrant> ToPermissionGrants(IReadOnlyList<ConsoleRbacGrant>? grants)
    {
        if (grants is null || grants.Count == 0)
        {
            return [];
        }

        var result = new List<PermissionGrant>(grants.Count);
        foreach (var grant in grants)
        {
            if (grant is null || string.IsNullOrWhiteSpace(grant.Permission)
                || string.Equals(grant.Grant, "not-granted", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var operation = grant.Permission switch
            {
                "manage-content" => "write",
                "manage-roles" => "admin",
                "share" => "share",
                "publish" => "publish",
                "view" => "read",
                _ => grant.Permission,
            };

            result.Add(new PermissionGrant { Service = "*", Layer = "*", Operation = operation });
        }

        return result;
    }

    /// <summary>Records a best-effort role-change audit event (resourceType "role") for the audit trail.</summary>
    private static Task RecordRoleAuditAsync(IAuditLog auditLog, HttpContext context, string action, string roleId, string roleName)
    {
        var actor = context.User?.Identity?.Name ?? AuditEvent.AnonymousActor;
        var auditEvent = new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.ConfigChange,
            Actor = actor,
            ActorType = string.Equals(actor, AuditEvent.AnonymousActor, StringComparison.Ordinal)
                ? AuditActorType.Anonymous
                : AuditActorType.UserId,
            ResourceType = "role",
            ResourceId = roleId,
            Action = action,
            Outcome = AuditOutcome.Success,
            CorrelationId = context.TraceIdentifier,
            Details = $"{{\"name\":\"{System.Text.Json.JsonEncodedText.Encode(roleName)}\"}}",
        };
        return auditLog.RecordAsync(auditEvent, context.RequestAborted);
    }

    /// <summary>Projects one global role definition into the workspace-scoped console role row.</summary>
    private static ConsoleRbacRole ProjectRole(RoleDefinition role) => new()
    {
        Id = role.RoleId.ToString(),
        Name = role.Name,
        Description = role.Description,
        IsCustom = !role.IsBuiltIn,
        Grants = PermissionColumns
            .Select(column => new ConsoleRbacGrant
            {
                Permission = column.Key,
                Grant = GrantFor(role, column.Key),
            })
            .ToArray(),
    };

    /// <summary>
    /// Folds the role's service/layer/operation grants into a single console permission column's grant kind.
    /// A wildcard operation ("*") or a column-matching operation grants the column; otherwise it is not granted.
    /// </summary>
    private static string GrantFor(RoleDefinition role, string permissionKey)
    {
        // The console column vocabulary maps onto the admin operation vocabulary:
        //   manage-content -> write   manage-roles -> admin   share -> share   publish -> publish   view -> read
        var operation = permissionKey switch
        {
            "manage-content" => "write",
            "manage-roles" => "admin",
            "share" => "share",
            "publish" => "publish",
            "view" => "read",
            _ => permissionKey,
        };

        foreach (var grant in role.Permissions)
        {
            if (grant.Operation == "*" ||
                string.Equals(grant.Operation, operation, StringComparison.OrdinalIgnoreCase))
            {
                // A layer/service-scoped grant is surfaced as scoped; a wildcard scope is a full grant.
                var fullScope = grant.Service == "*" && grant.Layer == "*";
                return fullScope ? "granted" : "scoped";
            }
        }

        return "not-granted";
    }
}
