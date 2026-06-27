// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Operations;

/// <summary>
/// HTTP surface for the Honua Operations Toolset — the single operation surface used by
/// the deterministic console UI (and, later, the AI orchestrator). Exposes the catalog
/// snapshot and a single invoke entry point that flows through the policy decision
/// point.
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
            .WithSummary("List the Honua operation catalog snapshot")
            .Produces<ApiResponse<OperationCatalogSnapshot>>();

        group.MapPost("/{operationId}/invoke", HandleInvokeOperation)
            .WithName("InvokeOperation")
            .WithSummary("Invoke a Honua operation through the policy decision point")
            .Produces<ApiResponse<OperationResult>>()
            .Produces<ApiResponse<object>>(StatusCodes.Status400BadRequest)
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

    private static async Task<IResult> HandleInvokeOperation(
        HttpContext context,
        string operationId,
        IOperationInvoker invoker,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);

        if (string.IsNullOrWhiteSpace(operationId))
        {
            return Results.Json(
                ApiResponse<object>.Failure("An operation identifier is required."),
                OperationsJsonContext.Default.ApiResponseObject,
                statusCode: StatusCodes.Status400BadRequest);
        }

        JsonElement? input = null;
        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Content-Type"))
        {
            try
            {
                var document = await JsonDocument
                    .ParseAsync(context.Request.Body, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                input = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return Results.Json(
                    ApiResponse<object>.Failure("The request body is not valid JSON."),
                    OperationsJsonContext.Default.ApiResponseObject,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        var invocation = new OperationInvocationContext
        {
            OperationId = operationId,
            Input = input,
            CallerIdentity = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.Identity?.Name,
            // Community is the only tier behaviour implemented in this spike (pass-through).
            Tier = OperationTier.Community,
            Role = context.User.FindFirstValue(ClaimTypes.Role)
        };

        var result = await invoker.InvokeAsync(invocation, cancellationToken).ConfigureAwait(false);

        var statusCode = result switch
        {
            { Success: true } => StatusCodes.Status200OK,
            { ErrorCode: "operation.not_found" } => StatusCodes.Status404NotFound,
            // Require-approval / dry-run-first / deny lanes are returned 200 with the
            // decision in the body; the lane is the contracted result, not an error.
            _ when result.Outcome != OperationPolicyOutcome.Allow => StatusCodes.Status200OK,
            _ => StatusCodes.Status400BadRequest
        };

        return Results.Json(
            ApiResponse<OperationResult>.CreateSuccess(result),
            OperationsJsonContext.Default.ApiResponseOperationResult,
            statusCode: statusCode);
    }

    private static void SetNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
    }
}
