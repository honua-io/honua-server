// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Features.RateLimiting.Abstractions;
using Honua.Core.Features.RateLimiting.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for rate limit policy configuration.
/// </summary>
internal static partial class RateLimitEndpoints
{
    /// <summary>
    /// Log category for rate limit endpoints.
    /// </summary>
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

    /// <summary>
    /// Configure rate limit policy admin endpoints.
    /// </summary>
    public static void MapRateLimitEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/rate-limits")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Rate Limits")
            .RequireAdminAuthorization();

        group.MapGet("/", HandleListPolicies)
            .WithDisplayName("List Rate Limit Policies")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/", HandleCreatePolicy)
            .WithDisplayName("Create Rate Limit Policy")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapGet("/{id:guid}", HandleGetPolicy)
            .WithDisplayName("Get Rate Limit Policy")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPut("/{id:guid}", HandleUpdatePolicy)
            .WithDisplayName("Update Rate Limit Policy")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapDelete("/{id:guid}", HandleDeletePolicy)
            .WithDisplayName("Delete Rate Limit Policy")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));

        group.MapGet("/status", HandleGetStatus)
            .WithDisplayName("Get Rate Limit Status")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

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
        catch (Exception ex)
        {
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
        catch (Exception ex)
        {
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
        catch (Exception ex)
        {
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
            var existing = await store.GetPolicyAsync(id, context.RequestAborted);
            if (existing == null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("Rate limit policy not found"));
            }

            var updated = new RateLimitPolicy
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
            };

            var result = await store.UpdatePolicyAsync(updated, context.RequestAborted);
            return TypedResults.Ok(ApiResponse<RateLimitPolicyResponse>.CreateSuccess(ToResponse(result!)));
        }
        catch (Exception ex)
        {
            RateLimitLog.UpdatePolicyFailed(logger, id, ex);
            return TypedResults.Problem(
                title: "Rate limit policy update failed",
                detail: "An internal error occurred while updating the rate limit policy.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
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
        catch (Exception ex)
        {
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
        catch (Exception ex)
        {
            RateLimitLog.GetStatusFailed(logger, key, ex);
            return TypedResults.Problem(
                title: "Rate limit status retrieval failed",
                detail: "An internal error occurred while retrieving rate limit status.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
