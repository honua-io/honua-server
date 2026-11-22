// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Asp.Versioning.Builder;
using Honua.Core.Features.RateLimiting.Abstractions;
using Honua.Core.Features.RateLimiting.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Honua.Server.Features.Admin.Services;
using Honua.Server.Features.Infrastructure.Configuration;
using Honua.Server.Features.Operations;

namespace Honua.Server.Features.Admin;

internal static partial class RateLimitEndpoints
{
    private const string RateLimitSettingsName = "Rate-limit policies";

    internal sealed class RateLimitEndpointsLog;

    internal static partial class RateLimitLog
    {
        [LoggerMessage(EventId = 4540, Level = LogLevel.Information,
            Message = "Listed {Count} rate limit policies")]
        public static partial void PoliciesListed(ILogger logger, int count);

        [LoggerMessage(EventId = 4541, Level = LogLevel.Information,
            Message = "Created rate limit policy '{Name}' with ID {PolicyId}")]
        public static partial void PolicyCreated(ILogger logger, string name, Guid policyId);

        [LoggerMessage(EventId = 4542, Level = LogLevel.Information,
            Message = "Deleted rate limit policy {PolicyId}")]
        public static partial void PolicyDeleted(ILogger logger, Guid policyId);
    }

    public static void MapRateLimitEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/rate-limits")
            .WithMetadata(TenantIndependentControlPlaneMetadata.Instance)
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Rate Limits")
            .RequireAdminAuthorization();

        var list = AdminHttpOperationRouteBinding.ForAccess("admin.rate-limit-policy.list", "/rate-limits");
        group.MapMethods(list.RoutePattern, list.Methods, HandleListPolicies)
            .WithAdminOperation(list.Definition).WithDisplayName("List Rate Limit Policies");

        var create = AdminHttpOperationRouteBinding.ForAccess("admin.rate-limit-policy.create", "/rate-limits");
        group.MapMethods(create.RoutePattern, create.Methods, HandleCreatePolicy)
            .WithAdminOperation(create.Definition).WithDisplayName("Create Rate Limit Policy")
            .RefuseWhenProcessLocalOnSeveralTasks<IRateLimitPolicyStore, InMemoryRateLimitPolicyStore>(RateLimitSettingsName);

        var get = AdminHttpOperationRouteBinding.ForAccess("admin.rate-limit-policy.get", "/rate-limits", "/{id:guid}");
        group.MapMethods(get.RoutePattern, get.Methods, HandleGetPolicy)
            .WithAdminOperation(get.Definition).WithDisplayName("Get Rate Limit Policy");

        var update = AdminHttpOperationRouteBinding.ForAccess("admin.rate-limit-policy.update", "/rate-limits", "/{id:guid}");
        group.MapMethods(update.RoutePattern, update.Methods, HandleUpdatePolicy)
            .WithAdminOperation(update.Definition).WithDisplayName("Update Rate Limit Policy")
            .RefuseWhenProcessLocalOnSeveralTasks<IRateLimitPolicyStore, InMemoryRateLimitPolicyStore>(RateLimitSettingsName);

        var delete = AdminHttpOperationRouteBinding.ForAccess("admin.rate-limit-policy.delete", "/rate-limits", "/{id:guid}");
        group.MapMethods(delete.RoutePattern, delete.Methods, HandleDeletePolicy)
            .WithAdminOperation(delete.Definition).WithDisplayName("Delete Rate Limit Policy")
            .RefuseWhenProcessLocalOnSeveralTasks<IRateLimitPolicyStore, InMemoryRateLimitPolicyStore>(RateLimitSettingsName);

        var status = AdminHttpOperationRouteBinding.ForAccess("admin.rate-limit-policy.status", "/rate-limits");
        group.MapMethods(status.RoutePattern, status.Methods, HandleGetStatus)
            .WithAdminOperation(status.Definition).WithDisplayName("Get Rate Limit Status");

        MapPolicyHistory(group);
    }

    private static void MapPolicyHistory(IVersionedEndpointRouteBuilder group)
    {
        var revisions = AdminHttpOperationRouteBinding.ForAccess("admin.rate-limit-policy.revisions", "/rate-limits", "/{id:guid}/revisions");
        group.MapMethods(revisions.RoutePattern, revisions.Methods, HandleListPolicyRevisions)
            .WithAdminOperation(revisions.Definition).WithDisplayName("List Rate Limit Policy Revisions");

        var undo = AdminHttpOperationRouteBinding.ForAccess("admin.rate-limit-policy.undo", "/rate-limits", "/{id:guid}/undo");
        group.MapMethods(undo.RoutePattern, undo.Methods, HandleUndoPolicyChange)
            .WithAdminOperation(undo.Definition).WithDisplayName("Undo Rate Limit Policy Change")
            .RefuseWhenProcessLocalOnSeveralTasks<IRateLimitPolicyStore, InMemoryRateLimitPolicyStore>(RateLimitSettingsName);
    }

    private static Task<Results<Ok<ApiResponse<IReadOnlyList<SettingsRevisionResponse>>>, NotFound<ApiResponse<object>>, Conflict<ApiResponse<object>>>>
        HandleListPolicyRevisions(Guid id, HttpContext context)
        => SettingsRevisionHandlers.ListAsync(PolicyDocument(id), context);

    // A deleted policy can be restored by undo: policies carry no entitlement or capacity limit.
    private static Task<Results<Ok<ApiResponse<SettingsRevisionResponse>>, BadRequest<ApiResponse<object>>, Conflict<ApiResponse<object>>>>
        HandleUndoPolicyChange(Guid id, UndoSettingsChangeRequest request, HttpContext context)
        => SettingsRevisionHandlers.UndoAsync(new(PolicyDocument(id), DeletedDocumentUndo.Recreate), request, context);

    private static ConfigurationDocumentKey PolicyDocument(Guid id) => new(PostgresRateLimitPolicyStore.DocumentKind, id.ToString("D"));

    private static RateLimitPolicyResponse ToResponse(RateLimitPolicy p) => new()
    {
        PolicyId = p.PolicyId,
        Name = p.Name,
        Scope = p.Scope,
        Key = p.Key,
        RequestsPerWindow = p.RequestsPerWindow,
        WindowDurationSeconds = p.WindowDuration.TotalSeconds,
        Enabled = p.Enabled,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
    };

    private static async Task<Results<Ok<ApiResponse<IReadOnlyList<RateLimitPolicyResponse>>>, ProblemHttpResult>>
        HandleListPolicies(
            [FromServices] IRateLimitPolicyStore store,
            [FromServices] ILogger<RateLimitEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var policies = await store.ListPoliciesAsync(context.RequestAborted);
            var response = policies.Select(ToResponse).ToList();
            RateLimitLog.PoliciesListed(logger, response.Count);
            IReadOnlyList<RateLimitPolicyResponse> readOnly = response.AsReadOnly();
            return TypedResults.Ok(ApiResponse<IReadOnlyList<RateLimitPolicyResponse>>.CreateSuccess(readOnly));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional catch-all request-handling boundary: logs and returns a generic
            // problem-details error response below.
            RateLimitLog.ListPoliciesFailed(logger, ex);
            return TypedResults.Problem(
                title: "Rate limit policy listing failed",
                detail: "An internal error occurred while listing rate limit policies.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Created<ApiResponse<RateLimitPolicyResponse>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>
        HandleCreatePolicy(
            CreateRateLimitPolicyRequest request,
            [FromServices] IRateLimitPolicyStore store,
            [FromServices] ILogger<RateLimitEndpointsLog> logger,
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

            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Any(char.IsControl))
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure(
                    "Validation failed: name must not be empty or contain control characters."));
            }

            if (!RateLimitScopes.IsKnown(request.Scope))
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure(
                    $"Unknown scope '{request.Scope}'. Valid tiers: '{RateLimitScopes.Plan}', '{RateLimitScopes.Tenant}', '{RateLimitScopes.ApiKey}', '{RateLimitScopes.Endpoint}'."));
            }

            var policy = new RateLimitPolicy
            {
                Name = request.Name,
                Scope = request.Scope,
                Key = request.Key,
                RequestsPerWindow = request.RequestsPerWindow,
                WindowDuration = TimeSpan.FromSeconds(request.WindowDurationSeconds),
                Enabled = request.Enabled,
            };

            var created = await store.CreatePolicyAsync(policy, context.RequestAborted);
            var response = ToResponse(created);

            RateLimitLog.PolicyCreated(logger, created.Name, created.PolicyId);
            return TypedResults.Created($"/api/v1/admin/rate-limits/{created.PolicyId}",
                ApiResponse<RateLimitPolicyResponse>.CreateSuccess(response));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional catch-all request-handling boundary: logs and returns a generic
            // problem-details error response below.
            RateLimitLog.CreatePolicyFailed(logger, ex);
            return TypedResults.Problem(
                title: "Rate limit policy creation failed",
                detail: "An internal error occurred while creating the rate limit policy.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<RateLimitPolicyResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleGetPolicy(
            Guid id,
            [FromServices] IRateLimitPolicyStore store,
            [FromServices] ILogger<RateLimitEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var policy = await store.GetPolicyAsync(id, context.RequestAborted);
            if (policy == null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("Rate limit policy not found"));
            }

            return TypedResults.Ok(ApiResponse<RateLimitPolicyResponse>.CreateSuccess(ToResponse(policy)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional catch-all request-handling boundary: logs and returns a generic
            // problem-details error response below.
            RateLimitLog.GetPolicyFailed(logger, id, ex);
            return TypedResults.Problem(
                title: "Rate limit policy retrieval failed",
                detail: "An internal error occurred while retrieving the rate limit policy.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<RateLimitPolicyResponse>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>
        HandleUpdatePolicy(
            Guid id,
            UpdateRateLimitPolicyRequest request,
            [FromServices] IRateLimitPolicyStore store,
            [FromServices] ILogger<RateLimitEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            if (ValidateUpdatePolicyRequest(request) is { } validationError)
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure(validationError));
            }

            var result = await store.UpdatePolicyAsync(id, existing => new RateLimitPolicy
            {
                PolicyId = existing.PolicyId,
                Name = request.Name ?? existing.Name,
                Scope = existing.Scope,
                Key = existing.Key,
                RequestsPerWindow = request.RequestsPerWindow ?? existing.RequestsPerWindow,
                WindowDuration = request.WindowDurationSeconds.HasValue
                    ? TimeSpan.FromSeconds(request.WindowDurationSeconds.Value)
                    : existing.WindowDuration,
                Enabled = request.Enabled ?? existing.Enabled,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            }, context.RequestAborted);
            if (result is null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("Rate limit policy not found"));
            }

            return TypedResults.Ok(ApiResponse<RateLimitPolicyResponse>.CreateSuccess(ToResponse(result!)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional catch-all request-handling boundary: logs and returns a generic
            // problem-details error response below.
            RateLimitLog.UpdatePolicyFailed(logger, id, ex);
            return TypedResults.Problem(
                title: "Rate limit policy update failed",
                detail: "An internal error occurred while updating the rate limit policy.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static string? ValidateUpdatePolicyRequest(UpdateRateLimitPolicyRequest request)
    {
        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(request, new ValidationContext(request), validationResults, true))
        {
            var errors = string.Join(", ", validationResults.Select(r => r.ErrorMessage));
            return $"Validation failed: {errors}";
        }

        if (request.Name is not null && (string.IsNullOrWhiteSpace(request.Name) || request.Name.Any(char.IsControl)))
        {
            return "Validation failed: name must not be empty or contain control characters.";
        }

        return null;
    }

    private static async Task<Results<Ok<ApiResponse<object>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleDeletePolicy(
            Guid id,
            [FromServices] IRateLimitPolicyStore store,
            [FromServices] ILogger<RateLimitEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var deleted = await store.DeletePolicyAsync(id, context.RequestAborted);
            if (!deleted)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("Rate limit policy not found"));
            }

            RateLimitLog.PolicyDeleted(logger, id);
            return TypedResults.Ok(ApiResponse<object>.SuccessWithMessage("Rate limit policy deleted"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional catch-all request-handling boundary: logs and returns a generic
            // problem-details error response below.
            RateLimitLog.DeletePolicyFailed(logger, id, ex);
            return TypedResults.Problem(
                title: "Rate limit policy deletion failed",
                detail: "An internal error occurred while deleting the rate limit policy.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<RateLimitStatusResponse>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>
        HandleGetStatus(
            [FromServices] IRateLimitPolicyStore store,
            [FromServices] ILogger<RateLimitEndpointsLog> logger,
            HttpContext context,
            [FromQuery] string? key = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure("Query parameter 'key' is required"));
            }

            var status = await store.GetStatusAsync(key, context.RequestAborted);
            if (status == null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("No rate limit policy found for the specified key"));
            }

            var response = new RateLimitStatusResponse
            {
                PolicyId = status.PolicyId,
                Key = status.Key,
                RequestsConsumed = status.RequestsConsumed,
                RequestsRemaining = status.RequestsRemaining,
                WindowResetsAt = status.WindowResetsAt,
            };

            return TypedResults.Ok(ApiResponse<RateLimitStatusResponse>.CreateSuccess(response));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional catch-all request-handling boundary: logs and returns a generic
            // problem-details error response below.
            RateLimitLog.GetStatusFailed(logger, key, ex);
            return TypedResults.Problem(
                title: "Rate limit status retrieval failed",
                detail: "An internal error occurred while retrieving rate limit status.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
