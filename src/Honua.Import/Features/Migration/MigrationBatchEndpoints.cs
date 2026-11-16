// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Import.FileImport;

namespace Honua.Migration;

internal static partial class MigrationBatchEndpoints
{
    private const int MaxFootprintLayers = 500;

    public static void MapMigrationBatchEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/import/migrations")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Import")
            .RequireAdminAuthorization();

        _ = group.MapPost("/", HandleStartBatch)
            .WithName("StartMigrationBatch")
            .WithSummary("Start an ordered, resumable batch migration run from a footprint selection");

        _ = group.MapGet("/{batchId}", HandleGetBatch)
            .WithName("GetMigrationBatch")
            .WithSummary("Get the rolled-up status and per-child progress of a batch migration run");
    }

    private static async Task HandleStartBatch(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;

        MigrationBatchStartApiRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                MigrationBatchJsonContext.Default.MigrationBatchStartApiRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally generic: ReadFromJsonAsync can throw JsonException, NotSupportedException,
        // or IOException for malformed/unreadable request bodies; map all of them to a 400 response.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.RequestDeserializationFailed(GetLogger(context), ex);
            await AdminResponseWriter.WriteErrorAsync(context, "Invalid request body", StatusCodes.Status400BadRequest);
            return;
        }

        if (request is null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Request body is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.SourceKind))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "sourceKind is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (request.Layers is null || request.Layers.Length == 0)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "At least one layer is required in the footprint", StatusCodes.Status400BadRequest);
            return;
        }

        if (request.Layers.Length > MaxFootprintLayers)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                $"A batch footprint may contain at most {MaxFootprintLayers} layers.",
                StatusCodes.Status400BadRequest);
            return;
        }

        var seenResourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in request.Layers)
        {
            if (string.IsNullOrWhiteSpace(layer.SourceResourceId))
            {
                await AdminResponseWriter.WriteErrorAsync(context, "Each layer requires a sourceResourceId", StatusCodes.Status400BadRequest);
                return;
            }

            if (!seenResourceIds.Add(layer.SourceResourceId))
            {
                await AdminResponseWriter.WriteErrorAsync(
                    context,
                    $"Duplicate sourceResourceId '{layer.SourceResourceId}' in footprint.",
                    StatusCodes.Status400BadRequest);
                return;
            }

            if (string.IsNullOrWhiteSpace(layer.ServiceUrl))
            {
                await AdminResponseWriter.WriteErrorAsync(context, "Each layer requires a serviceUrl", StatusCodes.Status400BadRequest);
                return;
            }

            if (string.IsNullOrWhiteSpace(layer.TableName) || !ImportValidationHelpers.IsValidTableName(layer.TableName))
            {
                await AdminResponseWriter.WriteErrorAsync(
                    context,
                    "Each layer requires a valid tableName (letters, numbers, and underscores only).",
                    StatusCodes.Status400BadRequest);
                return;
            }

            if (!string.IsNullOrWhiteSpace(layer.TargetSchema) && !ImportValidationHelpers.IsValidSchemaName(layer.TargetSchema))
            {
                await AdminResponseWriter.WriteErrorAsync(
                    context,
                    "Invalid target schema. Use only letters, numbers, and underscores.",
                    StatusCodes.Status400BadRequest);
                return;
            }

            if (!ImportValidationHelpers.IsPermittedTargetSchema(layer.TargetSchema, context.RequestServices))
            {
                await AdminResponseWriter.WriteErrorAsync(
                    context,
                    ImportTargetSchemaPolicy.NotOperationalSchemaMessage,
                    StatusCodes.Status400BadRequest);
                return;
            }

            var allowedHostSuffixes = context.RequestServices.GetAllowedMigrationServiceHostSuffixes();
            var urlValidation = await GeoservicesServiceUrlValidation.ValidateAsync(
                layer.ServiceUrl,
                allowedHostSuffixes,
                cancellationToken).ConfigureAwait(false);
            if (!urlValidation.IsValid)
            {
                await AdminResponseWriter.WriteErrorAsync(context, urlValidation.ErrorMessage!, StatusCodes.Status400BadRequest);
                return;
            }
        }

        var orchestrator = context.RequestServices.GetRequiredService<IMigrationBatchOrchestrator>();
        IReadOnlyList<GeoservicesImportTarget> targets;
        try
        {
            targets = await GeoservicesImportPlanning.PlanAsync(context, request.Layers.Select(layer => new GeoservicesImportSelection
            {
                ServiceUrl = layer.ServiceUrl!,
                ServiceName = layer.ServiceName,
                PublicName = layer.PublicName,
                FolderId = layer.FolderId,
                Namespace = layer.Namespace,
                Plan = layer.Plan
            }).ToArray()).ConfigureAwait(false);
        }
        catch (LayerPublishingException ex)
        {
            await GeoservicesImportPlanning.WriteErrorAsync(context, ex).ConfigureAwait(false);
            return;
        }
        var startRequest = new MigrationBatchStartRequest
        {
            SourceKind = request.SourceKind,
            SourceUrl = request.SourceUrl ?? string.Empty,
            SourceDisplayName = request.SourceDisplayName,
            ManifestBody = request.ManifestBody,
            ApplyRelationships = request.ApplyRelationships ?? false,
            RequireFullFidelity = request.RequireFullFidelity ?? false,
            Layers = request.Layers.Select((l, index) => new MigrationBatchLayerSpec
            {
                SourceResourceId = l.SourceResourceId!,
                ServiceUrl = l.ServiceUrl!,
                LayerId = l.LayerId,
                TableName = l.TableName!,
                TargetSchema = l.TargetSchema,
                ServiceName = targets[index].ServiceName,
                Target = targets[index],
                DependsOn = l.DependsOn ?? []
            }).ToArray()
        };

        MigrationBatchRunRecord batch;
        try
        {
            batch = await orchestrator.StartAsync(startRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                ImportValidationErrorMessage.FromArgument(ex, "Invalid migration batch request."),
                StatusCodes.Status400BadRequest);
            return;
        }
        catch (MigrationConstructAccountingRefusedException ex)
        {
            await Results.Json(
                    new MigrationBatchRefusedResponse { Error = ex.Message, ConstructAccounting = ex.Report },
                    MigrationBatchJsonContext.Default.MigrationBatchRefusedResponse,
                    statusCode: StatusCodes.Status422UnprocessableEntity)
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var catalog = context.RequestServices.GetRequiredService<IMigrationBatchRunCatalog>();
        var startedChildren = await catalog.GetChildrenAsync(batch.BatchId, cancellationToken).ConfigureAwait(false);
        var response = ToResponse(
            batch,
            [],
            await AccountAsync(catalog, batch, startedChildren, cancellationToken).ConfigureAwait(false));
        await Results.Json(response, MigrationBatchJsonContext.Default.MigrationBatchResponse, statusCode: StatusCodes.Status202Accepted)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task HandleGetBatch(HttpContext context)
    {
        var batchId = context.GetRouteValue("batchId")?.ToString();
        if (string.IsNullOrWhiteSpace(batchId))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Batch ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        var cancellationToken = context.RequestAborted;
        var catalog = context.RequestServices.GetRequiredService<IMigrationBatchRunCatalog>();
        var batch = await catalog.GetAsync(batchId, cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Batch migration run not found", StatusCodes.Status404NotFound);
            return;
        }

        var children = await catalog.GetChildrenAsync(batchId, cancellationToken).ConfigureAwait(false);
        var response = ToResponse(
            batch,
            children,
            await AccountAsync(catalog, batch, children, cancellationToken).ConfigureAwait(false));
        await Results.Json(response, MigrationBatchJsonContext.Default.MigrationBatchResponse)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task<MigrationConstructAccountingReport> AccountAsync(
        IMigrationBatchRunCatalog catalog,
        MigrationBatchRunRecord batch,
        IReadOnlyList<MigrationBatchChildRecord> children,
        CancellationToken cancellationToken)
    {
        var manifestBody = await catalog.GetManifestBodyAsync(batch.BatchId, cancellationToken).ConfigureAwait(false);
        return MigrationServiceConstructAccountant.Account(
            batch.SourceKind,
            manifestBody,
            MigrationBatchOrchestrator.ToConstructSelection(children));
    }

    private static MigrationBatchResponse ToResponse(
        MigrationBatchRunRecord batch,
        IReadOnlyList<MigrationBatchChildRecord> children,
        MigrationConstructAccountingReport constructAccounting) => new()
        {
            ConstructAccounting = constructAccounting,
            BatchId = batch.BatchId,
            SourceKind = batch.SourceKind,
            SourceUrl = batch.SourceUrl,
            SourceDisplayName = batch.SourceDisplayName,
            Status = BatchStatusToText(batch.Status),
            StartedAt = batch.StartedAt,
            CompletedAt = batch.CompletedAt,
            TotalChildren = batch.TotalChildren,
            SucceededChildren = batch.SucceededChildren,
            FailedChildren = batch.FailedChildren,
            CancelledChildren = batch.CancelledChildren,
            ApplyRelationships = batch.ApplyRelationships,
            RelationshipsApplied = batch.RelationshipsApplied,
            StatusNote = batch.StatusNote,
            FidelityVerdict = batch.FidelityVerdict,
            FidelityDifferences = batch.FidelityDifferences,
            Children = children.Select(static c => new MigrationBatchChildResponse
            {
                Ordinal = c.Ordinal,
                SourceResourceId = c.SourceResourceId,
                ServiceUrl = c.ServiceUrl,
                SourceLayerId = c.SourceLayerId,
                TableName = c.TableName,
                ServiceName = c.ServiceName,
                DependsOn = c.DependsOn.ToArray(),
                Status = ChildStatusToText(c.Status),
                JobId = c.JobId,
                PublishedLayerId = c.PublishedLayerId,
                StatusNote = c.StatusNote,
                FidelityVerdict = c.FidelityVerdict
            }).ToArray()
        };

    private static string BatchStatusToText(MigrationBatchRunStatus status) => status switch
    {
        MigrationBatchRunStatus.Running => "running",
        MigrationBatchRunStatus.Succeeded => "succeeded",
        MigrationBatchRunStatus.Failed => "failed",
        MigrationBatchRunStatus.Cancelled => "cancelled",
        MigrationBatchRunStatus.NeedsReview => "needs-review",
        _ => "running"
    };

    private static string ChildStatusToText(MigrationBatchChildStatus status) => status switch
    {
        MigrationBatchChildStatus.Pending => "pending",
        MigrationBatchChildStatus.Running => "running",
        MigrationBatchChildStatus.Succeeded => "succeeded",
        MigrationBatchChildStatus.Failed => "failed",
        MigrationBatchChildStatus.NeedsReview => "needs-review",
        MigrationBatchChildStatus.Cancelled => "cancelled",
        _ => "pending"
    };

    private static ILogger<MigrationBatchEndpointsLog> GetLogger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILogger<MigrationBatchEndpointsLog>>();

    internal sealed class MigrationBatchEndpointsLog;

    private static partial class Log
    {
        [LoggerMessage(8160, LogLevel.Warning, "Failed to deserialize migration batch start request")]
        public static partial void RequestDeserializationFailed(ILogger logger, Exception exception);
    }
}

public sealed record MigrationBatchStartApiRequest
{
    public string? SourceKind { get; init; }

    public string? SourceUrl { get; init; }

    public string? SourceDisplayName { get; init; }

    public MigrationBatchLayerApiSpec[]? Layers { get; init; }

    public string? ManifestBody { get; init; }

    public bool? ApplyRelationships { get; init; }

    public bool? RequireFullFidelity { get; init; }
}

public sealed record MigrationBatchRefusedResponse
{
    public required string Error { get; init; }

    public required MigrationConstructAccountingReport ConstructAccounting { get; init; }
}

public sealed record MigrationBatchLayerApiSpec
{
    public GeoservicesImportPlan? Plan { get; init; }

    public string? PublicName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public MetadataV2FolderSelection FolderId { get; init; }

    public string? Namespace { get; init; }

    public string? SourceResourceId { get; init; }

    public string? ServiceUrl { get; init; }

    public int LayerId { get; init; }

    public string? TableName { get; init; }

    public string? TargetSchema { get; init; }

    public string? ServiceName { get; init; }

    public string[]? DependsOn { get; init; }
}

public sealed record MigrationBatchResponse
{
    public required string BatchId { get; init; }

    public required string SourceKind { get; init; }

    public string SourceUrl { get; init; } = string.Empty;

    public string? SourceDisplayName { get; init; }

    public required string Status { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public int TotalChildren { get; init; }

    public int SucceededChildren { get; init; }

    public int FailedChildren { get; init; }

    public int CancelledChildren { get; init; }

    public bool ApplyRelationships { get; init; }

    public bool RelationshipsApplied { get; init; }

    public string? StatusNote { get; init; }

    public MigrationBatchChildResponse[] Children { get; init; } = [];

    public string? FidelityVerdict { get; init; }

    public MigrationFidelityDifference[] FidelityDifferences { get; init; } = [];

    public MigrationConstructAccountingReport? ConstructAccounting { get; init; }
}

public sealed record MigrationBatchChildResponse
{
    public int Ordinal { get; init; }

    public required string SourceResourceId { get; init; }

    public required string ServiceUrl { get; init; }

    public int SourceLayerId { get; init; }

    public required string TableName { get; init; }

    public string? ServiceName { get; init; }

    public string[] DependsOn { get; init; } = [];

    public required string Status { get; init; }

    public string? JobId { get; init; }

    public int? PublishedLayerId { get; init; }

    public string? StatusNote { get; init; }

    public string? FidelityVerdict { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MigrationBatchStartApiRequest))]
[JsonSerializable(typeof(MigrationBatchLayerApiSpec))]
[JsonSerializable(typeof(MigrationBatchLayerApiSpec[]))]
[JsonSerializable(typeof(MigrationBatchResponse))]
[JsonSerializable(typeof(MigrationBatchChildResponse))]
[JsonSerializable(typeof(MigrationBatchChildResponse[]))]
[JsonSerializable(typeof(MigrationBatchRefusedResponse))]
public sealed partial class MigrationBatchJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
