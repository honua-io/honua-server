// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for role and permission management.
/// </summary>
internal static partial class RoleEndpoints
{
    /// <summary>
    /// Log category for role endpoints.
    /// </summary>
    internal sealed class RoleEndpointsLog;

    internal static partial class RoleLog
    {
        [LoggerMessage(EventId = 4530, Level = LogLevel.Information,
            Message = "Listed {Count} roles")]
        public static partial void RolesListed(ILogger logger, int count);

        [LoggerMessage(EventId = 4531, Level = LogLevel.Information,
            Message = "Created role '{Name}' with ID {RoleId}")]
        public static partial void RoleCreated(ILogger logger, string name, Guid roleId);

        [LoggerMessage(EventId = 4532, Level = LogLevel.Information,
            Message = "Deleted role {RoleId}")]
        public static partial void RoleDeleted(ILogger logger, Guid roleId);

        [LoggerMessage(EventId = 4533, Level = LogLevel.Information,
            Message = "Updated permissions for role {RoleId}: {Count} grants")]
        public static partial void PermissionsUpdated(ILogger logger, Guid roleId, int count);
    }

    /// <summary>
    /// Configure role management admin endpoints.
    /// </summary>
    public static void MapRoleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/roles")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Roles")
            .RequireAdminAuthorization();

        group.MapGet("/", HandleListRoles)
            .WithDisplayName("List Roles")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/", HandleCreateRole)
            .WithDisplayName("Create Role")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapGet("/{id:guid}", HandleGetRole)
            .WithDisplayName("Get Role")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPut("/{id:guid}", HandleUpdateRole)
            .WithDisplayName("Update Role")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapDelete("/{id:guid}", HandleDeleteRole)
            .WithDisplayName("Delete Role")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));

        group.MapGet("/{id:guid}/permissions", HandleGetPermissions)
            .WithDisplayName("Get Role Permissions")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPut("/{id:guid}/permissions", HandleSetPermissions)
            .WithDisplayName("Set Role Permissions")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));
    }

    private static RoleResponse ToResponse(RoleDefinition r) => new()
    {
        RoleId = r.RoleId,
        Name = r.Name,
        Description = r.Description,
        IsBuiltIn = r.IsBuiltIn,
        Permissions = r.Permissions.Select(p => new PermissionGrantResponse
        {
            Service = p.Service,
            Layer = p.Layer,
            Operation = p.Operation,
        }).ToList(),
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
    };

    private static async Task<Results<Ok<ApiResponse<IReadOnlyList<RoleResponse>>>, ProblemHttpResult>>
        HandleListRoles(
            [FromServices] IRoleStore store,
            [FromServices] ILogger<RoleEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var roles = await store.ListRolesAsync(context.RequestAborted);
            var response = roles.Select(ToResponse).ToList();
            RoleLog.RolesListed(logger, response.Count);
            IReadOnlyList<RoleResponse> readOnly = response.AsReadOnly();
            return TypedResults.Ok(ApiResponse<IReadOnlyList<RoleResponse>>.CreateSuccess(readOnly));
        }
        catch (Exception ex)
        {
            RoleLog.ListRolesFailed(logger, ex);
            return TypedResults.Problem(
                title: "Role listing failed",
                detail: "An internal error occurred while listing roles.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Created<ApiResponse<RoleResponse>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>
        HandleCreateRole(
            CreateRoleRequest request,
            [FromServices] IRoleStore store,
            [FromServices] ILogger<RoleEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var validationResults = new List<ValidationResult>();
            if (!Validator.TryValidateObject(request, new ValidationContext(request), validationResults, true))
            {
                var errors = string.Join(", ", validationResults.Select(r => r.ErrorMessage));
                return TypedResults.BadRequest(ApiResponse<object>.Failure($"Validation failed: {errors}"));
            }

            var role = new RoleDefinition
            {
                Name = request.Name,
                Description = request.Description,
            };

            var created = await store.CreateRoleAsync(role, context.RequestAborted);
            var response = ToResponse(created);

            RoleLog.RoleCreated(logger, created.Name, created.RoleId);
            return TypedResults.Created($"/api/v1/admin/roles/{created.RoleId}",
                ApiResponse<RoleResponse>.CreateSuccess(response));
        }
        catch (Exception ex)
        {
            RoleLog.CreateRoleFailed(logger, ex);
            return TypedResults.Problem(
                title: "Role creation failed",
                detail: "An internal error occurred while creating the role.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<RoleResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleGetRole(
            Guid id,
            [FromServices] IRoleStore store,
            [FromServices] ILogger<RoleEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var role = await store.GetRoleAsync(id, context.RequestAborted);
            if (role == null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("Role not found"));
            }

            return TypedResults.Ok(ApiResponse<RoleResponse>.CreateSuccess(ToResponse(role)));
        }
        catch (Exception ex)
        {
            RoleLog.GetRoleFailed(logger, id, ex);
            return TypedResults.Problem(
                title: "Role retrieval failed",
                detail: "An internal error occurred while retrieving the role.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<RoleResponse>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>
        HandleUpdateRole(
            Guid id,
            UpdateRoleRequest request,
            [FromServices] IRoleStore store,
            [FromServices] ILogger<RoleEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var existing = await store.GetRoleAsync(id, context.RequestAborted);
            if (existing == null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("Role not found"));
            }

            var updated = new RoleDefinition
            {
                RoleId = existing.RoleId,
                Name = request.Name ?? existing.Name,
                Description = request.Description ?? existing.Description,
                IsBuiltIn = existing.IsBuiltIn,
                Permissions = existing.Permissions,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            var result = await store.UpdateRoleAsync(updated, context.RequestAborted);
            return TypedResults.Ok(ApiResponse<RoleResponse>.CreateSuccess(ToResponse(result!)));
        }
        catch (Exception ex)
        {
            RoleLog.UpdateRoleFailed(logger, id, ex);
            return TypedResults.Problem(
                title: "Role update failed",
                detail: "An internal error occurred while updating the role.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<object>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleDeleteRole(
            Guid id,
            [FromServices] IRoleStore store,
            [FromServices] ILogger<RoleEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var deleted = await store.DeleteRoleAsync(id, context.RequestAborted);
            if (!deleted)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("Role not found or is a built-in role"));
            }

            RoleLog.RoleDeleted(logger, id);
            return TypedResults.Ok(ApiResponse<object>.SuccessWithMessage("Role deleted"));
        }
        catch (Exception ex)
        {
            RoleLog.DeleteRoleFailed(logger, id, ex);
            return TypedResults.Problem(
                title: "Role deletion failed",
                detail: "An internal error occurred while deleting the role.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<IReadOnlyList<PermissionGrantResponse>>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleGetPermissions(
            Guid id,
            [FromServices] IRoleStore store,
            [FromServices] ILogger<RoleEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var role = await store.GetRoleAsync(id, context.RequestAborted);
            if (role == null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("Role not found"));
            }

            var permissions = await store.GetPermissionsAsync(id, context.RequestAborted);
            var response = permissions.Select(p => new PermissionGrantResponse
            {
                Service = p.Service,
                Layer = p.Layer,
                Operation = p.Operation,
            }).ToList();

            IReadOnlyList<PermissionGrantResponse> readOnly = response.AsReadOnly();
            return TypedResults.Ok(ApiResponse<IReadOnlyList<PermissionGrantResponse>>.CreateSuccess(readOnly));
        }
        catch (Exception ex)
        {
            RoleLog.GetPermissionsFailed(logger, id, ex);
            return TypedResults.Problem(
                title: "Permission retrieval failed",
                detail: "An internal error occurred while retrieving permissions.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<IReadOnlyList<PermissionGrantResponse>>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleSetPermissions(
            Guid id,
            SetPermissionsRequest request,
            [FromServices] IRoleStore store,
            [FromServices] ILogger<RoleEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var role = await store.GetRoleAsync(id, context.RequestAborted);
            if (role == null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("Role not found"));
            }

            var grants = request.Permissions.Select(p => new PermissionGrant
            {
                Service = p.Service,
                Layer = p.Layer,
                Operation = p.Operation,
            }).ToList();

            var result = await store.SetPermissionsAsync(id, grants, context.RequestAborted);

            var response = result.Select(p => new PermissionGrantResponse
            {
                Service = p.Service,
                Layer = p.Layer,
                Operation = p.Operation,
            }).ToList();

            RoleLog.PermissionsUpdated(logger, id, response.Count);
            IReadOnlyList<PermissionGrantResponse> readOnly = response.AsReadOnly();
            return TypedResults.Ok(ApiResponse<IReadOnlyList<PermissionGrantResponse>>.CreateSuccess(readOnly));
        }
        catch (Exception ex)
        {
            RoleLog.SetPermissionsFailed(logger, id, ex);
            return TypedResults.Problem(
                title: "Permission update failed",
                detail: "An internal error occurred while updating permissions.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
