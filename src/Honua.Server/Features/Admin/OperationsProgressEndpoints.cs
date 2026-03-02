// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Progress;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Unified operation progress endpoints for tracking any type of operation (upload, import, ingest, external import).
/// Replaces legacy progress endpoints with a single, consistent API.
/// </summary>
internal static class OperationsProgressEndpoints
{
    /// <summary>
    /// Map unified operation progress endpoints.
    /// </summary>
    public static void MapOperationsProgressEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/operations")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Operations Progress")
            .WithDescription("Unified operation progress tracking")
            .RequireAdminAuthorization();

        // Get operation status by ID
        _ = group.Map("/{operationId}", HandleGetOperationStatus)
            .WithName("GetOperationStatus")
            .WithSummary("Get the status of any operation by ID")
            .WithDescription("Returns progress information for upload, import, ingest, or external import operations")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]));

        // Cancel operation
        _ = group.Map("/{operationId}/cancel", HandleCancelOperation)
            .WithName("CancelOperation")
            .WithSummary("Cancel a running operation")
            .WithDescription("Attempts to cancel an operation that is currently queued or processing")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]));

        // List active operations by type
        _ = group.Map("/active", HandleListActiveOperations)
            .WithName("ListActiveOperations")
            .WithSummary("List all active operations")
            .WithDescription("Returns all currently active operations, optionally filtered by type")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]));

        // Get operations by type
        _ = group.Map("/type/{operationType}", HandleGetOperationsByType)
            .WithName("GetOperationsByType")
            .WithSummary("Get all operations of a specific type")
            .WithDescription("Returns all operations (active and completed) of the specified type")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]));
    }

    /// <summary>
    /// Get the status of any operation by its ID.
    /// </summary>
    private static async Task<IResult> HandleGetOperationStatus(
        string operationId,
        [FromServices] IUniversalProgressStore progressStore,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(operationId))
        {
            return Results.BadRequest(ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "Operation ID is required"));
        }

        var progress = await progressStore.GetProgressAsync(operationId, cancellationToken);

        if (progress == null)
        {
            return Results.NotFound(ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status404NotFound,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                $"Operation '{operationId}' not found"));
        }

        return CreateOperationStatusResult(progress);
    }

    private static IResult CreateOperationStatusResult(IOperationProgress progress)
    {
        return progress switch
        {
            UploadProgress uploadProgress => Results.Json(uploadProgress, OperationsProgressJsonContext.Default.UploadProgress),
            ImportProgress importProgress => Results.Json(importProgress, OperationsProgressJsonContext.Default.ImportProgress),
            IngestProgress ingestProgress => Results.Json(ingestProgress, OperationsProgressJsonContext.Default.IngestProgress),
            GeoservicesImportProgress externalImportProgress => Results.Json(externalImportProgress, OperationsProgressJsonContext.Default.GeoservicesImportProgress),
            TileOperationProgress tileOperationProgress => Results.Json(tileOperationProgress, OperationsProgressJsonContext.Default.TileOperationProgress),
            _ => Results.Json(progress, OperationsProgressJsonContext.Default.IOperationProgress)
        };
    }

    /// <summary>
    /// Cancel a running operation.
    /// </summary>
    private static async Task<IResult> HandleCancelOperation(
        string operationId,
        [FromServices] IUniversalProgressStore progressStore,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(operationId))
        {
            return Results.BadRequest(ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "Operation ID is required"));
        }

        var progress = await progressStore.GetProgressAsync(operationId, cancellationToken);

        if (progress == null)
        {
            return Results.NotFound(ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status404NotFound,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                $"Operation '{operationId}' not found"));
        }

        if (progress.Status is OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled)
        {
            return Results.BadRequest(ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "Operation is already completed, failed, or cancelled"));
        }

        if (progress is not ICancellableOperationProgress cancellableProgress)
        {
            return Results.BadRequest(ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                $"Operation type '{progress.Type}' does not support cancellation"));
        }

        var cancelledProgress = cancellableProgress.WithCancellation(DateTimeOffset.UtcNow, "Cancelled by user");

        await progressStore.SetProgressAsync(operationId, cancelledProgress,
            TimeSpan.FromHours(24), cancellationToken);

        var response = new CancelOperationResponse
        {
            OperationId = operationId,
            Message = "Operation cancellation requested",
            Type = progress.Type
        };

        return Results.Json(response, OperationsProgressJsonContext.Default.CancelOperationResponse);
    }

    /// <summary>
    /// List all active operations, optionally filtered by type.
    /// </summary>
    private static async Task<IResult> HandleListActiveOperations(
        [FromServices] IUniversalProgressStore progressStore,
        string? type = null,
        CancellationToken cancellationToken = default)
    {
        OperationType? operationType = null;

        if (!string.IsNullOrEmpty(type))
        {
            if (!Enum.TryParse<OperationType>(type, true, out var parsedType))
            {
                return Results.BadRequest(ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status400BadRequest,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                    $"Invalid operation type '{type}'. Valid types: {string.Join(", ", Enum.GetNames<OperationType>())}"));
            }
            operationType = parsedType;
        }

        var operationIds = await progressStore.GetActiveOperationIdsAsync(operationType, cancellationToken);
        var operations = new List<IOperationProgress>();

        foreach (var operationId in operationIds)
        {
            var progress = await progressStore.GetProgressAsync(operationId, cancellationToken);
            if (progress != null &&
                progress.Status is OperationStatus.Queued or OperationStatus.Processing)
            {
                operations.Add(progress);
            }
        }

        var response = new ActiveOperationsResponse
        {
            Operations = operations.ToArray(),
            TotalCount = operations.Count,
            FilteredByType = operationType
        };

        return Results.Json(response, OperationsProgressJsonContext.Default.ActiveOperationsResponse);
    }

    /// <summary>
    /// Get all operations of a specific type.
    /// </summary>
    private static async Task<IResult> HandleGetOperationsByType(
        string operationType,
        [FromServices] IUniversalProgressStore progressStore,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<OperationType>(operationType, true, out var parsedType))
        {
            return Results.BadRequest(ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                $"Invalid operation type '{operationType}'. Valid types: {string.Join(", ", Enum.GetNames<OperationType>())}"));
        }

        var operationIds = await progressStore.GetActiveOperationIdsAsync(parsedType, cancellationToken);
        var operations = new List<IOperationProgress>(operationIds.Count);

        foreach (var operationId in operationIds)
        {
            var progress = await progressStore.GetProgressAsync(operationId, cancellationToken);
            if (progress != null && progress.Type == parsedType)
            {
                operations.Add(progress);
            }
        }

        var response = new OperationsByTypeResponse
        {
            OperationType = parsedType,
            Operations = operations.ToArray(),
            TotalCount = operations.Count
        };

        return Results.Json(response, OperationsProgressJsonContext.Default.OperationsByTypeResponse);
    }
}

/// <summary>
/// Response for operation cancellation.
/// </summary>
internal sealed record CancelOperationResponse
{
    /// <summary>
    /// The operation ID that was cancelled.
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Confirmation message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Type of operation that was cancelled.
    /// </summary>
    public required OperationType Type { get; init; }
}

/// <summary>
/// Response containing list of active operations.
/// </summary>
internal sealed record ActiveOperationsResponse
{
    /// <summary>
    /// List of active operations.
    /// </summary>
    public required IOperationProgress[] Operations { get; init; }

    /// <summary>
    /// Total count of active operations.
    /// </summary>
    public required int TotalCount { get; init; }

    /// <summary>
    /// Operation type filter that was applied (if any).
    /// </summary>
    public OperationType? FilteredByType { get; init; }
}

/// <summary>
/// Response containing operations of a specific type.
/// </summary>
internal sealed record OperationsByTypeResponse
{
    /// <summary>
    /// The operation type.
    /// </summary>
    public required OperationType OperationType { get; init; }

    /// <summary>
    /// List of operations of this type.
    /// </summary>
    public required IOperationProgress[] Operations { get; init; }

    /// <summary>
    /// Total count of operations.
    /// </summary>
    public required int TotalCount { get; init; }
}

/// <summary>
/// JSON serialization context for operations progress endpoints.
/// </summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.General)]
[System.Text.Json.Serialization.JsonSerializable(typeof(IOperationProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(UploadProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ImportProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(IngestProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(GeoservicesImportProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TileOperationProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(CancelOperationResponse))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ActiveOperationsResponse))]
[System.Text.Json.Serialization.JsonSerializable(typeof(OperationsByTypeResponse))]
[System.Text.Json.Serialization.JsonSerializable(typeof(OperationType))]
[System.Text.Json.Serialization.JsonSerializable(typeof(OperationStatus))]
internal sealed partial class OperationsProgressJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
