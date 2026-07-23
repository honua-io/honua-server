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
/// Admin endpoints for field-level security (column masking) policy management (#1940).
/// Mirrors <see cref="RlsPolicyEndpoints"/> (#502); field-mask policies attach a masked
/// attribute to a (role, service, layer) scope and are dropped from query output for the
/// matching role at the shared projection seam.
/// </summary>
internal static partial class FieldMaskPolicyEndpoints
{
    internal sealed class FieldMaskPolicyEndpointsLog;

    internal static partial class FieldMaskPolicyLog
    {
        [LoggerMessage(EventId = 4550, Level = LogLevel.Information, Message = "Listed {Count} field-mask policies")]
        public static partial void PoliciesListed(ILogger logger, int count);

        [LoggerMessage(EventId = 4551, Level = LogLevel.Information,
            Message = "Created field-mask policy {PolicyId} for role '{Role}' on {Service}/{Layer} attribute '{Attribute}'")]
        public static partial void PolicyCreated(ILogger logger, Guid policyId, string role, string service, string layer, string attribute);

        [LoggerMessage(EventId = 4552, Level = LogLevel.Information, Message = "Deleted field-mask policy {PolicyId}")]
        public static partial void PolicyDeleted(ILogger logger, Guid policyId);

        [LoggerMessage(EventId = 4553, Level = LogLevel.Error, Message = "Field-mask policy operation failed")]
        public static partial void OperationFailed(ILogger logger, Exception exception);
    }

    /// <summary>
    /// Configure field-mask policy management admin endpoints.
    /// </summary>
    public static void MapFieldMaskPolicyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/field-mask-policies")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "FieldMask")
            .RequireAdminAuthorization();

        group.MapGet("/", HandleListPolicies)
            .WithDisplayName("List Field-Mask Policies")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/", HandleCreatePolicy)
            .WithDisplayName("Create Field-Mask Policy")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapGet("/{id:guid}", HandleGetPolicy)
            .WithDisplayName("Get Field-Mask Policy")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapDelete("/{id:guid}", HandleDeletePolicy)
            .WithDisplayName("Delete Field-Mask Policy")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));
    }

    private static FieldMaskPolicyResponse ToResponse(FieldMaskPolicy p) => new()
    {
        PolicyId = p.PolicyId,
        Role = p.Role,
        Service = p.Service,
        Layer = p.Layer,
        Attribute = p.Attribute,
        Description = p.Description,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
    };

    private static async Task<Results<Ok<ApiResponse<IReadOnlyList<FieldMaskPolicyResponse>>>, ProblemHttpResult>>
        HandleListPolicies(
            [FromServices] IFieldMaskPolicyStore store,
            [FromServices] ILogger<FieldMaskPolicyEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var policies = await store.ListPoliciesAsync(context.RequestAborted);
            var response = policies.Select(ToResponse).ToList();
            FieldMaskPolicyLog.PoliciesListed(logger, response.Count);
            IReadOnlyList<FieldMaskPolicyResponse> readOnly = response.AsReadOnly();
            return TypedResults.Ok(ApiResponse<IReadOnlyList<FieldMaskPolicyResponse>>.CreateSuccess(readOnly));
        }
        // Intentional broad catch: this is the request-handling boundary for the field-mask policy
        // list endpoint; the failure is logged and mapped to a generic problem response below.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FieldMaskPolicyLog.OperationFailed(logger, ex);
            return TypedResults.Problem(
                title: "Field-mask policy listing failed",
                detail: "An internal error occurred while listing field-mask policies.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<FieldMaskPolicyResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleGetPolicy(
            Guid id,
            [FromServices] IFieldMaskPolicyStore store,
            [FromServices] ILogger<FieldMaskPolicyEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var policy = await store.GetPolicyAsync(id, context.RequestAborted);
            if (policy is null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("Field-mask policy not found"));
            }

            return TypedResults.Ok(ApiResponse<FieldMaskPolicyResponse>.CreateSuccess(ToResponse(policy)));
        }
        // Intentional broad catch: this is the request-handling boundary for the field-mask policy
        // get endpoint; the failure is logged and mapped to a generic problem response below.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FieldMaskPolicyLog.OperationFailed(logger, ex);
            return TypedResults.Problem(
                title: "Field-mask policy retrieval failed",
                detail: "An internal error occurred while retrieving the field-mask policy.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Created<ApiResponse<FieldMaskPolicyResponse>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>
        HandleCreatePolicy(
            CreateFieldMaskPolicyRequest request,
            [FromServices] IFieldMaskPolicyStore store,
            [FromServices] ILogger<FieldMaskPolicyEndpointsLog> logger,
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

            var policy = new FieldMaskPolicy
            {
                Role = request.Role.Trim(),
                Service = request.Service.Trim(),
                Layer = request.Layer.Trim(),
                Attribute = request.Attribute.Trim(),
                Description = request.Description,
            };

            var created = await store.CreatePolicyAsync(policy, context.RequestAborted);
            FieldMaskPolicyLog.PolicyCreated(logger, created.PolicyId, created.Role, created.Service, created.Layer, created.Attribute);
            return TypedResults.Created(
                $"/api/v1/admin/field-mask-policies/{created.PolicyId}",
                ApiResponse<FieldMaskPolicyResponse>.CreateSuccess(ToResponse(created)));
        }
        catch (InvalidOperationException ex)
        {
            // Duplicate scope (unique-constraint violation) is a client error.
            return TypedResults.BadRequest(ApiResponse<object>.Failure(ex.Message));
        }
        // Intentional broad catch: this is the request-handling boundary for the field-mask policy
        // create endpoint (after the more specific InvalidOperationException case above); the
        // failure is logged and mapped to a generic problem response below.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FieldMaskPolicyLog.OperationFailed(logger, ex);
            return TypedResults.Problem(
                title: "Field-mask policy creation failed",
                detail: "An internal error occurred while creating the field-mask policy.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<object>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleDeletePolicy(
            Guid id,
            [FromServices] IFieldMaskPolicyStore store,
            [FromServices] ILogger<FieldMaskPolicyEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var deleted = await store.DeletePolicyAsync(id, context.RequestAborted);
            if (!deleted)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("Field-mask policy not found"));
            }

            FieldMaskPolicyLog.PolicyDeleted(logger, id);
            return TypedResults.Ok(ApiResponse<object>.SuccessWithMessage("Field-mask policy deleted"));
        }
        // Intentional broad catch: this is the request-handling boundary for the field-mask policy
        // delete endpoint; the failure is logged and mapped to a generic problem response below.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FieldMaskPolicyLog.OperationFailed(logger, ex);
            return TypedResults.Problem(
                title: "Field-mask policy deletion failed",
                detail: "An internal error occurred while deleting the field-mask policy.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
