// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Operations;

/// <summary>
/// HTTP surface for the Honua Operations Toolset (deterministic, no AI): list the grounding
/// catalog, validate and submit operations through the policy-gated dispatcher, and read a
/// submitted handle's status. Admin-authorized.
/// </summary>
internal static class OperationsEndpoints
{
    internal sealed class OperationsEndpointsLog;

    public static void MapOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/operations")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Operations")
            .RequireAdminAuthorization();

        group.MapGet("/", HandleListOperations)
            .WithName("ListOperations")
            .WithSummary("List the operation grounding catalog")
            .Produces<ApiResponse<OperationCatalogSnapshot>>();

        group.MapPost("/{id}/validate", HandleValidateOperation)
            .WithName("ValidateOperation")
            .WithSummary("Validate (plan) an operation without side effects")
            .Accepts<OperationInvokeRequest>("application/json")
            .Produces<ApiResponse<OperationValidation>>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id}/submit", HandleSubmitOperation)
            .WithName("SubmitOperation")
            .WithSummary("Submit an operation through the policy-gated dispatcher")
            .Accepts<OperationInvokeRequest>("application/json")
            .Produces<ApiResponse<OperationHandle>>()
            .Produces(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/handles/{handleId}", HandleGetHandleStatus)
            .WithName("GetOperationHandleStatus")
            .WithSummary("Get the status of a submitted operation handle")
            .Produces<ApiResponse<OperationStatus>>()
            .Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> HandleListOperations(
        HttpContext context,
        IOperationCatalog catalog,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        var snapshot = await catalog.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        context.Response.Headers.ETag = $"\"{snapshot.CatalogVersion}\"";
        return Results.Json(
            ApiResponse<OperationCatalogSnapshot>.CreateSuccess(snapshot),
            OperationsJsonContext.Default.ApiResponseOperationCatalogSnapshot);
    }

    private static async Task<IResult> HandleValidateOperation(
        HttpContext context,
        string id,
        OperationInvokeRequest request,
        IOperationInvoker invoker,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        try
        {
            var validation = await invoker
                .ValidateAsync(ToRequest(id, request), cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(
                ApiResponse<OperationValidation>.CreateSuccess(validation),
                OperationsJsonContext.Default.ApiResponseOperationValidation);
        }
        catch (OperationNotFoundException)
        {
            return NotFound(context, $"Operation '{id}' was not found.");
        }
        catch (ArgumentException ex)
        {
            return BadRequest(context, ex.Message);
        }
        catch (ResourceNotFoundException)
        {
            return NotFound(context, "The requested resource was not found.");
        }
    }

    private static async Task<IResult> HandleSubmitOperation(
        HttpContext context,
        string id,
        OperationInvokeRequest request,
        IOperationInvoker invoker,
        OperationHandleStore handleStore,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);

        // ApprovalModel=OperatorGate: publish operations flow through the operator approval
        // gate at the HTTP layer, matching LayerPublishingEndpoints. The policy decision point
        // (Community pass-through today) is consulted again inside the dispatcher.
        var gate = context.RequestServices.GetRequiredService<OperatorApprovalGate>();
        var approvalResult = gate.EvaluateApproval(
            context, OperatorResourceType.Catalog, OperatorOperation.Publish);
        if (approvalResult != null)
        {
            return approvalResult;
        }

        try
        {
            var policyContext = new OperationPolicyContext
            {
                PrincipalId = context.User.Identity?.Name
            };

            var handle = await invoker
                .SubmitAsync(ToRequest(id, request), policyContext, cancellationToken)
                .ConfigureAwait(false);
            handleStore.Record(handle);

            return Results.Json(
                ApiResponse<OperationHandle>.CreateSuccess(handle),
                OperationsJsonContext.Default.ApiResponseOperationHandle);
        }
        catch (OperationNotFoundException)
        {
            return NotFound(context, $"Operation '{id}' was not found.");
        }
        catch (ArgumentException ex)
        {
            return BadRequest(context, ex.Message);
        }
        catch (ResourceNotFoundException)
        {
            return NotFound(context, "The requested resource was not found.");
        }
    }

    private static IResult HandleGetHandleStatus(
        HttpContext context,
        string handleId,
        OperationHandleStore handleStore)
    {
        SetNoStore(context);
        var handle = handleStore.Get(handleId);
        if (handle is null)
        {
            return NotFound(context, $"Operation handle '{handleId}' was not found.");
        }

        var status = new OperationStatus
        {
            OperationId = handle.OperationId,
            HandleId = handle.HandleId,
            Status = handle.Status,
            Result = handle.Result,
            JobId = handle.JobId,
            MetadataRevision = handle.MetadataRevision
        };

        return Results.Json(
            ApiResponse<OperationStatus>.CreateSuccess(status),
            OperationsJsonContext.Default.ApiResponseOperationStatus);
    }

    private static OperationRequest ToRequest(string operationId, OperationInvokeRequest request)
        => new()
        {
            OperationId = operationId,
            Parameters = request.Parameters,
            ConnectionId = request.ConnectionId,
            ServiceName = request.ServiceName,
            Fields = request.Fields,
            DryRun = request.DryRun
        };

    private static IResult BadRequest(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(
            context,
            StatusCodes.Status400BadRequest,
            ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
            detail);

    private static IResult NotFound(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(
            context,
            StatusCodes.Status404NotFound,
            ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
            detail);

    private static void SetNoStore(HttpContext context)
        => context.Response.Headers.CacheControl = "no-store";
}
