// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for user identity management.
/// </summary>
internal static partial class UserManagementEndpoints
{
    /// <summary>
    /// Log category for user management endpoints.
    /// </summary>
    internal sealed class UserManagementEndpointsLog;

    internal static partial class UserManagementLog
    {
        [LoggerMessage(EventId = 4520, Level = LogLevel.Information,
            Message = "Listed {Count} users (total: {TotalCount})")]
        public static partial void UsersListed(ILogger logger, int count, int totalCount);

        [LoggerMessage(EventId = 4521, Level = LogLevel.Information,
            Message = "Updated roles for user '{UserId}': {RoleCount} roles assigned")]
        public static partial void UserRolesUpdated(ILogger logger, string userId, int roleCount);

        [LoggerMessage(EventId = 4522, Level = LogLevel.Information,
            Message = "Deprovisioned user '{UserId}'")]
        public static partial void UserDeprovisioned(ILogger logger, string userId);

        [LoggerMessage(EventId = 4523, Level = LogLevel.Information,
            Message = "Resolved effective permissions for user '{UserId}': {PermissionCount} permissions")]
        public static partial void EffectivePermissionsResolved(ILogger logger, string userId, int permissionCount);
    }

    /// <summary>
    /// Configure user management admin endpoints.
    /// </summary>
    public static void MapUserManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/users")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Users")
            .RequireAdminAuthorization();

        group.MapGet("/", HandleListUsers)
            .WithDisplayName("List Users")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/{id}", HandleGetUser)
            .WithDisplayName("Get User")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPut("/{id}/roles", HandleUpdateUserRoles)
            .WithDisplayName("Update User Roles")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapDelete("/{id}", HandleDeleteUser)
            .WithDisplayName("Deprovision User")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));

        group.MapGet("/{id}/effective-permissions", HandleGetEffectivePermissions)
            .WithDisplayName("Get User Effective Permissions")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    private static UserResponse ToResponse(ManagedUser u) => new()
    {
        UserId = u.UserId,
        DisplayName = u.DisplayName,
        Email = u.Email,
        ProvisioningSource = u.ProvisioningSource,
        ProviderId = u.ProviderId,
        IsActive = u.IsActive,
        Roles = u.Roles,
        CreatedAt = u.CreatedAt,
        UpdatedAt = u.UpdatedAt,
    };

    private static async Task<Results<Ok<ApiResponse<UserListResponse>>, ProblemHttpResult>>
        HandleListUsers(
            [FromServices] IUserStore store,
            [FromServices] ILogger<UserManagementEndpointsLog> logger,
            HttpContext context,
            [FromQuery] string? source = null,
            [FromQuery] string? role = null,
            [FromQuery] bool? active = null,
            [FromQuery] int limit = 100,
            [FromQuery] int offset = 0)
    {
        try
        {
            var filter = new UserListFilter
            {
                ProvisioningSource = source,
                Role = role,
                IsActive = active,
                Limit = limit,
                Offset = offset,
            };

            var result = await store.ListUsersAsync(filter, context.RequestAborted);
            var response = new UserListResponse
            {
                Users = result.Users.Select(ToResponse).ToList(),
                TotalCount = result.TotalCount,
            };

            UserManagementLog.UsersListed(logger, response.Users.Count, response.TotalCount);
            return TypedResults.Ok(ApiResponse<UserListResponse>.CreateSuccess(response));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list users");
            return TypedResults.Problem(
                title: "User listing failed",
                detail: "An internal error occurred while listing users.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<UserResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleGetUser(
            string id,
            [FromServices] IUserStore store,
            [FromServices] ILogger<UserManagementEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var user = await store.GetUserAsync(id, context.RequestAborted);
            if (user == null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("User not found"));
            }

            return TypedResults.Ok(ApiResponse<UserResponse>.CreateSuccess(ToResponse(user)));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get user {UserId}", id);
            return TypedResults.Problem(
                title: "User retrieval failed",
                detail: "An internal error occurred while retrieving the user.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<UserResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleUpdateUserRoles(
            string id,
            UpdateUserRolesRequest request,
            [FromServices] IUserStore store,
            [FromServices] ILogger<UserManagementEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var updated = await store.UpdateUserRolesAsync(id, request.Roles, context.RequestAborted);
            if (updated == null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("User not found"));
            }

            UserManagementLog.UserRolesUpdated(logger, id, request.Roles.Count);
            return TypedResults.Ok(ApiResponse<UserResponse>.CreateSuccess(ToResponse(updated)));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update roles for user {UserId}", id);
            return TypedResults.Problem(
                title: "User role update failed",
                detail: "An internal error occurred while updating user roles.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<object>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleDeleteUser(
            string id,
            [FromServices] IUserStore store,
            [FromServices] ILogger<UserManagementEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var deleted = await store.DeprovisionUserAsync(id, context.RequestAborted);
            if (!deleted)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("User not found"));
            }

            UserManagementLog.UserDeprovisioned(logger, id);
            return TypedResults.Ok(ApiResponse<object>.SuccessWithMessage("User deprovisioned"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to deprovision user {UserId}", id);
            return TypedResults.Problem(
                title: "User deprovisioning failed",
                detail: "An internal error occurred while deprovisioning the user.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<EffectivePermissionsResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleGetEffectivePermissions(
            string id,
            [FromServices] IUserStore userStore,
            [FromServices] IRoleStore roleStore,
            [FromServices] ILogger<UserManagementEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var user = await userStore.GetUserAsync(id, context.RequestAborted);
            if (user == null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("User not found"));
            }

            var effective = await roleStore.GetEffectivePermissionsAsync(id, user.Roles, context.RequestAborted);

            var response = new EffectivePermissionsResponse
            {
                UserId = effective.UserId,
                Roles = effective.Roles,
                Permissions = effective.Permissions.Select(p => new PermissionGrantResponse
                {
                    Service = p.Service,
                    Layer = p.Layer,
                    Operation = p.Operation,
                }).ToList(),
                ResolvedAt = effective.ResolvedAt,
            };

            UserManagementLog.EffectivePermissionsResolved(logger, id, response.Permissions.Count);
            return TypedResults.Ok(ApiResponse<EffectivePermissionsResponse>.CreateSuccess(response));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve effective permissions for user {UserId}", id);
            return TypedResults.Problem(
                title: "Effective permissions resolution failed",
                detail: "An internal error occurred while resolving effective permissions.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
