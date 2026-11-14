// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Core.Features.Orchestration.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Temporal.Domain;
using Honua.Infrastructure.Authentication;
using Honua.ControlPlane;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Progress;
using Honua.Infrastructure;
using Microsoft.AspNetCore.Mvc;

using Honua.Server.Features.Admin.TileOperations;

namespace Honua.Server.Features.Admin;

internal static partial class OperationsProgressEndpoints
{
    private static async Task<IResult> HandleGetOperationStatus(
        string operationId,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var progressStore = httpContext.RequestServices.GetRequiredService<IUniversalProgressStore>();
        var jobStore = httpContext.RequestServices.GetService<IExecutionJobStore>();
        if (string.IsNullOrEmpty(operationId))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "Operation ID is required");
        }

        var progress = await new ExecutionJobProgressReader(progressStore, jobStore).ReadAsync(operationId, cancellationToken);

        if (progress == null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status404NotFound,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status404NotFound),
                $"Operation '{operationId}' not found");
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
            ExportProgress exportProgress => Results.Json(exportProgress, OperationsProgressJsonContext.Default.ExportProgress),
            PrintProgress printProgress => Results.Json(printProgress, OperationsProgressJsonContext.Default.PrintProgress),
            RasterImportProgress rasterImportProgress => Results.Json(rasterImportProgress, OperationsProgressJsonContext.Default.RasterImportProgress),
            GeoprocessingProgress geoprocessingProgress => Results.Json(geoprocessingProgress, OperationsProgressJsonContext.Default.GeoprocessingProgress),
            PublishingProgress publishingProgress => Results.Json(publishingProgress, OperationsProgressJsonContext.Default.PublishingProgress),
            WorkflowProgress workflowProgress => Results.Json(workflowProgress, OperationsProgressJsonContext.Default.WorkflowProgress),
            DeploymentProgress deploymentProgress => Results.Json(deploymentProgress, OperationsProgressJsonContext.Default.DeploymentProgress),
            TemporalCorrectiveJobProgress temporalCorrectiveJobProgress => Results.Json(temporalCorrectiveJobProgress, OperationsProgressJsonContext.Default.TemporalCorrectiveJobProgress),
            _ => Results.Json(progress, OperationsProgressJsonContext.Default.IOperationProgress)
        };
    }

    private static JsonElement SerializeOperationToElement(IOperationProgress progress)
    {
        var serialized = progress switch
        {
            UploadProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.UploadProgress),
            ImportProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.ImportProgress),
            IngestProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.IngestProgress),
            GeoservicesImportProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.GeoservicesImportProgress),
            TileOperationProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.TileOperationProgress),
            ExportProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.ExportProgress),
            PrintProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.PrintProgress),
            RasterImportProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.RasterImportProgress),
            GeoprocessingProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.GeoprocessingProgress),
            PublishingProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.PublishingProgress),
            WorkflowProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.WorkflowProgress),
            DeploymentProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.DeploymentProgress),
            TemporalCorrectiveJobProgress p => JsonSerializer.SerializeToElement(p, OperationsProgressJsonContext.Default.TemporalCorrectiveJobProgress),
            _ => JsonSerializer.SerializeToElement(progress, OperationsProgressJsonContext.Default.IOperationProgress)
        };

        var operation = JsonNode.Parse(serialized.GetRawText())?.AsObject() ?? [];

        // List payloads need stable cross-operation keys for generic admin clients
        // while still preserving the type-specific fields from each concrete progress DTO.
        NormalizeCanonicalProperty(operation, "operationId", JsonValue.Create(progress.OperationId));
        NormalizeCanonicalProperty(operation, "type", JsonValue.Create((int)progress.Type));

        // JsonObject has no reflection metadata when the shipped image sets
        // JsonSerializerIsReflectionEnabledByDefault=false. The parameterless overload
        // then throws, and the list fails only once an operation is actually present.
        return JsonSerializer.SerializeToElement(operation, OperationsProgressJsonContext.Default.JsonObject);
    }

    internal static async Task<IResult> HandleListActiveOperations(
        HttpContext httpContext,
        string? type = null,
        CancellationToken cancellationToken = default)
    {
        var progressStore = httpContext.RequestServices.GetRequiredService<IUniversalProgressStore>();
        var jobStore = httpContext.RequestServices.GetService<IExecutionJobStore>();
        OperationType? operationType = null;

        if (!string.IsNullOrEmpty(type))
        {
            if (!Enum.TryParse<OperationType>(type, true, out var parsedType))
            {
                return ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status400BadRequest,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                    $"Invalid operation type '{type}'. Valid types: {string.Join(", ", Enum.GetNames<OperationType>())}");
            }
            operationType = parsedType;
        }

        var operationIds = await progressStore.GetActiveOperationIdsAsync(operationType, cancellationToken);
        var operations = new List<JsonElement>();

        await foreach (var storedProgress in ProgressStoreReader.ReadAvailableAsync(
            progressStore, operationIds, cancellationToken))
        {
            var progress = await new ExecutionJobProgressReader(progressStore, jobStore)
                .RepairAsync(storedProgress, cancellationToken).ConfigureAwait(false);
            if (progress.Status is OperationStatus.Queued or OperationStatus.Processing)
            {
                operations.Add(SerializeOperationToElement(progress));
            }
        }

        return ActiveOperationsResult(operations, operationType);
    }

    internal static async Task<IResult> HandleGetOperationsByType(
        string operationType,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var progressStore = httpContext.RequestServices.GetRequiredService<IUniversalProgressStore>();
        var jobStore = httpContext.RequestServices.GetService<IExecutionJobStore>();
        if (!Enum.TryParse<OperationType>(operationType, true, out var parsedType))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                $"Invalid operation type '{operationType}'. Valid types: {string.Join(", ", Enum.GetNames<OperationType>())}");
        }

        var operationIds = await progressStore.GetActiveOperationIdsAsync(parsedType, cancellationToken);
        var operations = new List<JsonElement>(operationIds.Count);

        await foreach (var storedProgress in ProgressStoreReader.ReadAvailableAsync(
            progressStore, operationIds, cancellationToken))
        {
            var progress = await new ExecutionJobProgressReader(progressStore, jobStore)
                .RepairAsync(storedProgress, cancellationToken).ConfigureAwait(false);
            if (progress.Type == parsedType)
            {
                operations.Add(SerializeOperationToElement(progress));
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
    private static IResult ActiveOperationsResult(List<JsonElement> operations, OperationType? operationType)
    {
        var response = new ActiveOperationsResponse
        {
            Operations = operations.ToArray(),
            TotalCount = operations.Count,
            FilteredByType = operationType
        };
        return Results.Json(response, OperationsProgressJsonContext.Default.ActiveOperationsResponse);
    }
}

internal sealed record CancelOperationResponse
{
    public required string OperationId { get; init; }

    public required string Message { get; init; }

    public required OperationType Type { get; init; }
}

internal sealed record ActiveOperationsResponse
{
    public required JsonElement[] Operations { get; init; }

    public required int TotalCount { get; init; }

    public OperationType? FilteredByType { get; init; }
}

internal sealed record OperationsByTypeResponse
{
    public required OperationType OperationType { get; init; }

    public required JsonElement[] Operations { get; init; }

    public required int TotalCount { get; init; }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.General)]
[System.Text.Json.Serialization.JsonSerializable(typeof(IOperationProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(UploadProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ImportProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(IngestProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(GeoservicesImportProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TileOperationProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ExportProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(PrintProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(RasterImportProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(RasterImportPhase))]
[System.Text.Json.Serialization.JsonSerializable(typeof(GeoprocessingProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(GeoprocessingWorkflowStatus))]
[System.Text.Json.Serialization.JsonSerializable(typeof(GeoprocessingStageKind))]
[System.Text.Json.Serialization.JsonSerializable(typeof(GeoprocessingStageStatus))]
[System.Text.Json.Serialization.JsonSerializable(typeof(PublishingProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(PublishIntentStatus))]
[System.Text.Json.Serialization.JsonSerializable(typeof(WorkflowProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(WorkflowRunStatus))]
[System.Text.Json.Serialization.JsonSerializable(typeof(DeploymentProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(DeploymentStatus))]
[System.Text.Json.Serialization.JsonSerializable(typeof(RolloutState))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TemporalCorrectiveJobProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(CancelOperationResponse))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ActiveOperationsResponse))]
[System.Text.Json.Serialization.JsonSerializable(typeof(OperationsByTypeResponse))]
[System.Text.Json.Serialization.JsonSerializable(typeof(OperationType))]
[System.Text.Json.Serialization.JsonSerializable(typeof(OperationStatus))]
[System.Text.Json.Serialization.JsonSerializable(typeof(System.Text.Json.Nodes.JsonObject))]
internal sealed partial class OperationsProgressJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
