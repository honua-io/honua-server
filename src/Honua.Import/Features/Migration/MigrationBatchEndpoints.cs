// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Import.FileImport;

namespace Honua.Migration;

/// <summary>
/// Issue #1253. Admin endpoints that start and observe footprint-driven batch
/// migration runs. <c>POST /api/v1/admin/import/migrations</c> initiates an
/// ordered, resumable batch from a footprint/selection and returns a batch run
/// id; <c>GET /api/v1/admin/import/migrations/{batchId}</c> returns the rolled-up
/// batch status plus per-child progress. Both are thin adapters over
/// <see cref="IMigrationBatchOrchestrator"/> and <see cref="IMigrationBatchRunCatalog"/>.
/// </summary>
internal static partial class MigrationBatchEndpoints
{
    private const int MaxFootprintLayers = 500;

    /// <summary>
    /// Maps the batch import orchestration endpoints under
    /// <c>/api/v1/admin/import/migrations</c>.
    /// </summary>
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
        catch (Exception)
        {
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

            var urlValidation = await GeoservicesServiceUrlValidation.ValidateAsync(layer.ServiceUrl, cancellationToken).ConfigureAwait(false);
            if (!urlValidation.IsValid)
            {
                await AdminResponseWriter.WriteErrorAsync(context, urlValidation.ErrorMessage!, StatusCodes.Status400BadRequest);
                return;
            }
        }

        var orchestrator = context.RequestServices.GetRequiredService<IMigrationBatchOrchestrator>();
        var startRequest = new MigrationBatchStartRequest
        {
            SourceKind = request.SourceKind,
            SourceUrl = request.SourceUrl ?? string.Empty,
            SourceDisplayName = request.SourceDisplayName,
            ManifestBody = request.ManifestBody,
            ApplyRelationships = request.ApplyRelationships ?? false,
            Layers = request.Layers.Select(static l => new MigrationBatchLayerSpec
            {
                SourceResourceId = l.SourceResourceId!,
                ServiceUrl = l.ServiceUrl!,
                LayerId = l.LayerId,
                TableName = l.TableName!,
                TargetSchema = l.TargetSchema,
                ServiceName = l.ServiceName,
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
            await AdminResponseWriter.WriteErrorAsync(context, ex.Message, StatusCodes.Status400BadRequest);
            return;
        }

        var response = ToResponse(batch, []);
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
        var response = ToResponse(batch, children);
        await Results.Json(response, MigrationBatchJsonContext.Default.MigrationBatchResponse)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static MigrationBatchResponse ToResponse(
        MigrationBatchRunRecord batch,
        IReadOnlyList<MigrationBatchChildRecord> children) => new()
        {
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
                StatusNote = c.StatusNote
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
}

/// <summary>
/// Request body for <c>POST /api/v1/admin/import/migrations</c>.
/// </summary>
public sealed record MigrationBatchStartApiRequest
{
    /// <summary>Source kind identifier such as <c>arcgis-geoservices-rest</c>.</summary>
    public string? SourceKind { get; init; }

    /// <summary>Redacted source URL describing the footprint origin (display only).</summary>
    public string? SourceUrl { get; init; }

    /// <summary>Optional operator-visible source display name.</summary>
    public string? SourceDisplayName { get; init; }

    /// <summary>Footprint layer-import specifications.</summary>
    public MigrationBatchLayerApiSpec[]? Layers { get; init; }

    /// <summary>Optional manifest JSON body used for post-publish relationship application (#1256).</summary>
    public string? ManifestBody { get; init; }

    /// <summary>Whether to apply manifest relationship classes after all layers publish.</summary>
    public bool? ApplyRelationships { get; init; }
}

/// <summary>
/// One layer-import specification in a batch footprint request.
/// </summary>
public sealed record MigrationBatchLayerApiSpec
{
    /// <summary>Stable source resource id (must match manifest ids for relationship-apply).</summary>
    public string? SourceResourceId { get; init; }

    /// <summary>Source ArcGIS service URL.</summary>
    public string? ServiceUrl { get; init; }

    /// <summary>Source layer id within the service.</summary>
    public int LayerId { get; init; }

    /// <summary>Target PostGIS table name.</summary>
    public string? TableName { get; init; }

    /// <summary>Optional target schema for imported operational data.</summary>
    public string? TargetSchema { get; init; }

    /// <summary>Optional target Honua service name for auto-publishing.</summary>
    public string? ServiceName { get; init; }

    /// <summary>Source resource ids this layer depends on (imported first).</summary>
    public string[]? DependsOn { get; init; }
}

/// <summary>
/// Response for batch start and batch status. Mirrors the batch run aggregate
/// plus per-child progress.
/// </summary>
public sealed record MigrationBatchResponse
{
    /// <summary>Stable batch id.</summary>
    public required string BatchId { get; init; }

    /// <summary>Source kind.</summary>
    public required string SourceKind { get; init; }

    /// <summary>Redacted source URL.</summary>
    public string SourceUrl { get; init; } = string.Empty;

    /// <summary>Operator-visible source display name.</summary>
    public string? SourceDisplayName { get; init; }

    /// <summary>Rolled-up batch status: running, succeeded, failed, cancelled, needs-review.</summary>
    public required string Status { get; init; }

    /// <summary>UTC start instant.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>UTC completion instant (null while running).</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Total child layer imports in the footprint.</summary>
    public int TotalChildren { get; init; }

    /// <summary>Number of succeeded children.</summary>
    public int SucceededChildren { get; init; }

    /// <summary>Number of failed children.</summary>
    public int FailedChildren { get; init; }

    /// <summary>Number of cancelled children.</summary>
    public int CancelledChildren { get; init; }

    /// <summary>Whether relationship-apply was requested.</summary>
    public bool ApplyRelationships { get; init; }

    /// <summary>Whether relationship-apply has run.</summary>
    public bool RelationshipsApplied { get; init; }

    /// <summary>Operator-visible note.</summary>
    public string? StatusNote { get; init; }

    /// <summary>Per-child progress, ordered by execution ordinal.</summary>
    public MigrationBatchChildResponse[] Children { get; init; } = [];
}

/// <summary>
/// Per-child progress within a <see cref="MigrationBatchResponse"/>.
/// </summary>
public sealed record MigrationBatchChildResponse
{
    /// <summary>Zero-based execution ordinal.</summary>
    public int Ordinal { get; init; }

    /// <summary>Stable source resource id.</summary>
    public required string SourceResourceId { get; init; }

    /// <summary>Source ArcGIS service URL.</summary>
    public required string ServiceUrl { get; init; }

    /// <summary>Source layer id.</summary>
    public int SourceLayerId { get; init; }

    /// <summary>Target PostGIS table name.</summary>
    public required string TableName { get; init; }

    /// <summary>Optional target Honua service name.</summary>
    public string? ServiceName { get; init; }

    /// <summary>Source resource ids this child depends on.</summary>
    public string[] DependsOn { get; init; } = [];

    /// <summary>Child status: pending, running, succeeded, failed, needs-review, cancelled.</summary>
    public required string Status { get; init; }

    /// <summary>Per-layer import job id once queued.</summary>
    public string? JobId { get; init; }

    /// <summary>Resolved Honua layer id once the child publishes.</summary>
    public int? PublishedLayerId { get; init; }

    /// <summary>Operator-visible note recorded on failure or review.</summary>
    public string? StatusNote { get; init; }
}

/// <summary>
/// Source-generated JSON serialization context for batch migration API types.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MigrationBatchStartApiRequest))]
[JsonSerializable(typeof(MigrationBatchLayerApiSpec))]
[JsonSerializable(typeof(MigrationBatchLayerApiSpec[]))]
[JsonSerializable(typeof(MigrationBatchResponse))]
[JsonSerializable(typeof(MigrationBatchChildResponse))]
[JsonSerializable(typeof(MigrationBatchChildResponse[]))]
public sealed partial class MigrationBatchJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
