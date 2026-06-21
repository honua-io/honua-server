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
/// Admin endpoints for row-level security (RLS) policy management (#502, epic #1275).
/// </summary>
internal static partial class RlsPolicyEndpoints
{
    internal sealed class RlsPolicyEndpointsLog;

    internal static partial class RlsPolicyLog
    {
        [LoggerMessage(EventId = 4540, Level = LogLevel.Information, Message = "Listed {Count} RLS policies")]
        public static partial void PoliciesListed(ILogger logger, int count);

        [LoggerMessage(EventId = 4541, Level = LogLevel.Information,
            Message = "Created RLS policy {PolicyId} for role '{Role}' on {Service}/{Layer}")]
        public static partial void PolicyCreated(ILogger logger, Guid policyId, string role, string service, string layer);

        [LoggerMessage(EventId = 4542, Level = LogLevel.Information, Message = "Deleted RLS policy {PolicyId}")]
        public static partial void PolicyDeleted(ILogger logger, Guid policyId);

        [LoggerMessage(EventId = 4543, Level = LogLevel.Error, Message = "RLS policy operation failed")]
        public static partial void OperationFailed(ILogger logger, Exception exception);
    }

    /// <summary>
    /// Configure RLS policy management admin endpoints.
    /// </summary>
    public static void MapRlsPolicyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/rls-policies")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "RLS")
            .RequireAdminAuthorization();

        group.MapGet("/", HandleListPolicies)
            .WithDisplayName("List RLS Policies")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/", HandleCreatePolicy)
            .WithDisplayName("Create RLS Policy")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapGet("/{id:guid}", HandleGetPolicy)
            .WithDisplayName("Get RLS Policy")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapDelete("/{id:guid}", HandleDeletePolicy)
            .WithDisplayName("Delete RLS Policy")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));
    }

    private static RlsPolicyResponse ToResponse(RlsPolicy p) => new()
    {
        PolicyId = p.PolicyId,
        Role = p.Role,
        Service = p.Service,
        Layer = p.Layer,
        Attribute = p.Attribute,
        ClaimType = p.ClaimType,
        Comparison = p.Comparison == RlsComparison.Equals ? "equals" : "in",
        Description = p.Description,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
    };

    private static async Task<Results<Ok<ApiResponse<IReadOnlyList<RlsPolicyResponse>>>, ProblemHttpResult>>
        HandleListPolicies(
            [FromServices] IRlsPolicyStore store,
            [FromServices] ILogger<RlsPolicyEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var policies = await store.ListPoliciesAsync(context.RequestAborted);
            var response = policies.Select(ToResponse).ToList();
            RlsPolicyLog.PoliciesListed(logger, response.Count);
            IReadOnlyList<RlsPolicyResponse> readOnly = response.AsReadOnly();
            return TypedResults.Ok(ApiResponse<IReadOnlyList<RlsPolicyResponse>>.CreateSuccess(readOnly));
        }
        catch (Exception ex)
        {
            RlsPolicyLog.OperationFailed(logger, ex);
            return TypedResults.Problem(
                title: "RLS policy listing failed",
                detail: "An internal error occurred while listing RLS policies.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<RlsPolicyResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleGetPolicy(
            Guid id,
            [FromServices] IRlsPolicyStore store,
            [FromServices] ILogger<RlsPolicyEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var policy = await store.GetPolicyAsync(id, context.RequestAborted);
            if (policy is null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("RLS policy not found"));
            }

            return TypedResults.Ok(ApiResponse<RlsPolicyResponse>.CreateSuccess(ToResponse(policy)));
        }
        catch (Exception ex)
        {
            RlsPolicyLog.OperationFailed(logger, ex);
            return TypedResults.Problem(
                title: "RLS policy retrieval failed",
                detail: "An internal error occurred while retrieving the RLS policy.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Created<ApiResponse<RlsPolicyResponse>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>
        HandleCreatePolicy(
            CreateRlsPolicyRequest request,
            [FromServices] IRlsPolicyStore store,
            [FromServices] ILogger<RlsPolicyEndpointsLog> logger,
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

            if (!TryParseComparison(request.Comparison, out var comparison))
            {
                return TypedResults.BadRequest(
                    ApiResponse<object>.Failure("Comparison must be 'in' or 'equals'."));
            }

            var policy = new RlsPolicy
            {
                Role = request.Role.Trim(),
                Service = request.Service.Trim(),
                Layer = request.Layer.Trim(),
                Attribute = request.Attribute.Trim(),
                ClaimType = request.ClaimType.Trim(),
                Comparison = comparison,
                Description = request.Description,
            };

            var created = await store.CreatePolicyAsync(policy, context.RequestAborted);
            RlsPolicyLog.PolicyCreated(logger, created.PolicyId, created.Role, created.Service, created.Layer);
            return TypedResults.Created(
                $"/api/v1/admin/rls-policies/{created.PolicyId}",
                ApiResponse<RlsPolicyResponse>.CreateSuccess(ToResponse(created)));
        }
        catch (InvalidOperationException ex)
        {
            // Duplicate scope (unique-constraint violation) is a client error.
            return TypedResults.BadRequest(ApiResponse<object>.Failure(ex.Message));
        }
        catch (Exception ex)
        {
            RlsPolicyLog.OperationFailed(logger, ex);
            return TypedResults.Problem(
                title: "RLS policy creation failed",
                detail: "An internal error occurred while creating the RLS policy.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<object>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleDeletePolicy(
            Guid id,
            [FromServices] IRlsPolicyStore store,
            [FromServices] ILogger<RlsPolicyEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var deleted = await store.DeletePolicyAsync(id, context.RequestAborted);
            if (!deleted)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("RLS policy not found"));
            }

            RlsPolicyLog.PolicyDeleted(logger, id);
            return TypedResults.Ok(ApiResponse<object>.SuccessWithMessage("RLS policy deleted"));
        }
        catch (Exception ex)
        {
            RlsPolicyLog.OperationFailed(logger, ex);
            return TypedResults.Problem(
                title: "RLS policy deletion failed",
                detail: "An internal error occurred while deleting the RLS policy.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static bool TryParseComparison(string? value, out RlsComparison comparison)
    {
        comparison = RlsComparison.In;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "in":
                comparison = RlsComparison.In;
                return true;
            case "equals":
            case "eq":
            case "=":
                comparison = RlsComparison.Equals;
                return true;
            default:
                return false;
        }
    }
}
