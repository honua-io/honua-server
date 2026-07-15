// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.Server.Features.Admin.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin editing surface for batched, transactional network-topology edge and
/// turn-restriction content edits against a non-active generation (#2716). Complements
/// <see cref="NetworkDatasetAdminEndpoints"/> (registry mapping/metadata CRUD) and the
/// #2715 versioned generation lifecycle: this surface allocates <c>draft</c> generations
/// and applies bounded, idempotent, optimistically-concurrent batch edits that atomically
/// transition a generation to <c>dirty</c>. It never changes a dataset's active generation
/// pointer, and topology rebuild/promotion/rollback remain out of scope (#2718/#2719/#2720).
/// The NAServer/GeoServices protocol adapter never calls this surface and stays read-only.
/// </summary>
internal static partial class NetworkTopologyEditAdminEndpoints
{
    private const string TagAdmin = "Admin";
    private const string TagNetworkDatasets = "Network Datasets";
    private const string IdempotencyKeyHeader = "Idempotency-Key";
    private const string IfMatchHeader = "If-Match";
    private const int MaxIdempotencyKeyLength = 200;

    /// <summary>
    /// Maps the network-topology edit admin endpoints into the request pipeline.
    /// </summary>
    /// <remarks>
    /// No-op when <see cref="INetworkTopologyEditStore"/> or
    /// <see cref="INetworkTopologyGenerationStore"/> are not registered (non-Postgres data
    /// providers), mirroring <see cref="NetworkDatasetAdminEndpoints"/>.
    /// </remarks>
    public static IEndpointRouteBuilder MapNetworkTopologyEditAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var serviceInspector = endpoints.ServiceProvider.GetService<IServiceProviderIsService>();
        if (serviceInspector is not null &&
            (!serviceInspector.IsService(typeof(INetworkTopologyEditStore)) ||
             !serviceInspector.IsService(typeof(INetworkTopologyGenerationStore))))
        {
            return endpoints;
        }

        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/network-datasets/{id}/generations")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags(TagAdmin, TagNetworkDatasets)
            .RequireAdminAuthorization();

        _ = group.MapGet("/", HandleListGenerations)
            .WithName("ListNetworkTopologyGenerations")
            .WithSummary("List topology generations for a network dataset.")
            .Produces<NetworkTopologyGenerationDto[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        _ = group.MapPost("/", HandleAllocateDraftGeneration)
            .WithName("AllocateNetworkTopologyDraftGeneration")
            .WithSummary("Allocate a new draft topology generation for edits.")
            .Produces<NetworkTopologyGenerationDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        _ = group.MapPost("/{generation:long}/edits", HandleApplyEditBatch)
            .WithName("ApplyNetworkTopologyEditBatch")
            .WithSummary("Apply a batched edge and turn-restriction content edit to a non-active topology generation.")
            .Accepts<NetworkTopologyEditBatchRequest>("application/json")
            .Produces<NetworkTopologyEditResultDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> HandleListGenerations(
        string id,
        HttpContext context,
        [FromServices] INetworkTopologyGenerationStore generationStore,
        [FromServices] ILogger<NetworkTopologyEditAdminLogCategory> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var generations = await generationStore.ListAsync(id, cancellationToken).ConfigureAwait(false);
            var dtos = new NetworkTopologyGenerationDto[generations.Count];
            for (var i = 0; i < generations.Count; i++)
            {
                dtos[i] = ToDto(generations[i]);
            }

            NetworkTopologyEditAdminLog.ListedGenerations(logger, id, dtos.Length);
            return Results.Json(dtos, NetworkTopologyEditAdminJsonContext.Default.NetworkTopologyGenerationDtoArray);
        }
        catch (Exception ex)
        {
            NetworkTopologyEditAdminLog.OperationFailed(logger, "list-generations", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to list topology generations.");
        }
    }

    private static async Task<IResult> HandleAllocateDraftGeneration(
        string id,
        HttpContext context,
        [FromServices] INetworkTopologyGenerationStore generationStore,
        [FromServices] ILogger<NetworkTopologyEditAdminLogCategory> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var generation = await generationStore.AllocateDraftAsync(id, cancellationToken).ConfigureAwait(false);
            NetworkTopologyEditAdminLog.AllocatedDraft(logger, id, generation.Generation);
            return Results.Json(
                ToDto(generation),
                NetworkTopologyEditAdminJsonContext.Default.NetworkTopologyGenerationDto,
                statusCode: StatusCodes.Status201Created);
        }
        catch (NetworkTopologyActiveGenerationMissingException)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound,
                "Network dataset not found or has no active topology generation.");
        }
        catch (Exception ex)
        {
            NetworkTopologyEditAdminLog.OperationFailed(logger, "allocate-draft", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to allocate a draft topology generation.");
        }
    }

    private static async Task<IResult> HandleApplyEditBatch(
        string id,
        long generation,
        HttpContext context,
        [FromServices] INetworkDatasetResolver datasetResolver,
        [FromServices] INetworkTopologyGenerationStore generationStore,
        [FromServices] INetworkTopologyEditStore editStore,
        [FromServices] ILogger<NetworkTopologyEditAdminLogCategory> logger,
        CancellationToken cancellationToken)
    {
        if (!TryResolveIdempotencyKey(context, out var idempotencyKey, out var headerError) ||
            !TryResolveExpectedRowVersion(context, out var expectedRowVersion, out headerError))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, headerError);
        }

        NetworkTopologyGeneration? targetGeneration;
        try
        {
            targetGeneration = await generationStore.GetAsync(id, generation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            NetworkTopologyEditAdminLog.OperationFailed(logger, "edit-batch.lookup-generation", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to load the topology generation.");
        }

        if (targetGeneration is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound,
                "Topology generation not found.");
        }

        NetworkDataset? dataset;
        try
        {
            dataset = await datasetResolver.ResolveAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            NetworkTopologyEditAdminLog.OperationFailed(logger, "edit-batch.resolve-dataset", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to resolve the network dataset.");
        }

        if (dataset is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound,
                "Network dataset not found.");
        }

        var allowedAttributeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in dataset.TravelProfiles)
        {
            allowedAttributeKeys.Add(profile.ForwardCostColumn);
            allowedAttributeKeys.Add(profile.ReverseCostColumn);
        }

        byte[] bodyBytes;
        using (var buffer = new MemoryStream())
        {
            await context.Request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bodyBytes = buffer.ToArray();
        }

        NetworkTopologyEditBatchRequest? requestDto;
        try
        {
            requestDto = JsonSerializer.Deserialize(bodyBytes, NetworkTopologyEditAdminJsonContext.Default.NetworkTopologyEditBatchRequest);
        }
        catch (JsonException)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "Request body is not valid JSON.");
        }

        if (requestDto is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest,
                "Request body is required.");
        }

        if (!TryMapBatch(requestDto, out var batch, out var mapError))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, mapError);
        }

        if (!NetworkTopologyEditValidation.TryValidateBatch(batch, targetGeneration.Srid, allowedAttributeKeys, out var validationError))
        {
            NetworkTopologyEditAdminLog.ValidationFailed(logger, id, generation, validationError);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, validationError);
        }

        var contentHash = Convert.ToHexString(SHA256.HashData(bodyBytes));
        var actor = ResolveActor(context);

        try
        {
            var result = await editStore.ApplyEditBatchAsync(
                    id,
                    generation,
                    expectedRowVersion,
                    idempotencyKey,
                    contentHash,
                    batch,
                    actor,
                    cancellationToken)
                .ConfigureAwait(false);

            NetworkTopologyEditAdminLog.EditApplied(
                logger,
                id,
                generation,
                actor,
                context.TraceIdentifier,
                batch.EdgeItemCount,
                batch.RestrictionItemCount,
                result.WasIdempotentReplay);

            context.Response.Headers.ETag = $"\"{result.RowVersion}\"";
            return Results.Json(ToDto(result), NetworkTopologyEditAdminJsonContext.Default.NetworkTopologyEditResultDto);
        }
        catch (NetworkTopologyGenerationNotFoundException)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound,
                "Topology generation not found.");
        }
        catch (NetworkTopologyEditConflictException ex)
        {
            NetworkTopologyEditAdminLog.ConflictRejected(logger, id, generation, ex.Reason.ToString(), actor);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status409Conflict, ex.Message);
        }
        catch (NetworkTopologyEditValidationException ex)
        {
            NetworkTopologyEditAdminLog.ValidationFailed(logger, id, generation, ex.Message);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (Exception ex)
        {
            NetworkTopologyEditAdminLog.OperationFailed(logger, "edit-batch.apply", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to apply the topology edit batch.");
        }
    }

    private static bool TryResolveIdempotencyKey(HttpContext context, out string key, out string error)
    {
        key = string.Empty;
        error = string.Empty;

        if (!context.Request.Headers.TryGetValue(IdempotencyKeyHeader, out var values) || values.Count == 0)
        {
            error = $"{IdempotencyKeyHeader} header is required.";
            return false;
        }

        var raw = values[0];
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = $"{IdempotencyKeyHeader} header must not be empty.";
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > MaxIdempotencyKeyLength)
        {
            error = $"{IdempotencyKeyHeader} header must be at most {MaxIdempotencyKeyLength} characters.";
            return false;
        }

        if (trimmed.Any(char.IsControl))
        {
            error = $"{IdempotencyKeyHeader} header must not contain control characters.";
            return false;
        }

        key = trimmed;
        return true;
    }

    private static bool TryResolveExpectedRowVersion(HttpContext context, out long rowVersion, out string error)
    {
        rowVersion = 0;
        error = string.Empty;

        if (!context.Request.Headers.TryGetValue(IfMatchHeader, out var values) || values.Count == 0)
        {
            error = $"{IfMatchHeader} header is required and must carry the generation's current row version.";
            return false;
        }

        var raw = values[0]?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(raw) || !long.TryParse(raw, out rowVersion) || rowVersion <= 0)
        {
            error = $"{IfMatchHeader} header must be a positive integer row version.";
            return false;
        }

        return true;
    }

    private static bool TryMapBatch(NetworkTopologyEditBatchRequest request, out NetworkTopologyEditBatch batch, out string error)
    {
        batch = NetworkTopologyEditBatch.Empty;

        var addEdges = new List<NetworkEdgeEdit>();
        foreach (var dto in request.AddEdges ?? [])
        {
            if (!TryMapEdge(dto, out var edge, out error))
            {
                return false;
            }

            addEdges.Add(edge);
        }

        var updateEdges = new List<NetworkEdgeEdit>();
        foreach (var dto in request.UpdateEdges ?? [])
        {
            if (!TryMapEdge(dto, out var edge, out error))
            {
                return false;
            }

            updateEdges.Add(edge);
        }

        var deleteEdgeIds = (request.DeleteEdgeIds ?? []).ToList();

        var addRestrictions = new List<NetworkTurnRestrictionEdit>();
        foreach (var dto in request.AddRestrictions ?? [])
        {
            if (!TryMapRestriction(dto, out var restriction, out error))
            {
                return false;
            }

            addRestrictions.Add(restriction);
        }

        var updateRestrictions = new List<NetworkTurnRestrictionEdit>();
        foreach (var dto in request.UpdateRestrictions ?? [])
        {
            if (!TryMapRestriction(dto, out var restriction, out error))
            {
                return false;
            }

            updateRestrictions.Add(restriction);
        }

        var deleteRestrictionIds = (request.DeleteRestrictionIds ?? []).ToList();

        batch = new NetworkTopologyEditBatch(
            addEdges, updateEdges, deleteEdgeIds,
            addRestrictions, updateRestrictions, deleteRestrictionIds);
        error = string.Empty;
        return true;
    }

    private static bool TryMapEdge(NetworkEdgeEditDto dto, out NetworkEdgeEdit edge, out string error)
    {
        edge = null!;

        if (string.IsNullOrWhiteSpace(dto.EdgeId))
        {
            error = "edge id is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(dto.SourceVertexId) || string.IsNullOrWhiteSpace(dto.TargetVertexId))
        {
            error = $"edge '{dto.EdgeId}' requires sourceVertexId and targetVertexId.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(dto.GeometryGeoJson))
        {
            error = $"edge '{dto.EdgeId}' requires geometryGeoJson.";
            return false;
        }

        if (dto.Srid is not { } srid)
        {
            error = $"edge '{dto.EdgeId}' requires srid.";
            return false;
        }

        edge = new NetworkEdgeEdit(
            dto.EdgeId,
            dto.SourceVertexId,
            dto.TargetVertexId,
            dto.GeometryGeoJson,
            srid,
            dto.Attributes ?? new Dictionary<string, string?>());
        error = string.Empty;
        return true;
    }

    private static bool TryMapRestriction(NetworkTurnRestrictionEditDto dto, out NetworkTurnRestrictionEdit restriction, out string error)
    {
        restriction = null!;

        if (string.IsNullOrWhiteSpace(dto.RestrictionId))
        {
            error = "turn restriction id is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(dto.FromEdgeId) || string.IsNullOrWhiteSpace(dto.ViaVertexId) || string.IsNullOrWhiteSpace(dto.ToEdgeId))
        {
            error = $"turn restriction '{dto.RestrictionId}' requires fromEdgeId, viaVertexId, and toEdgeId.";
            return false;
        }

        if (!TryParseRestrictionKind(dto.Kind, out var kind))
        {
            error = $"turn restriction '{dto.RestrictionId}' has an invalid kind '{dto.Kind}'; expected prohibited, required, or penalty.";
            return false;
        }

        restriction = new NetworkTurnRestrictionEdit(
            dto.RestrictionId,
            dto.FromEdgeId,
            dto.ViaVertexId,
            dto.ToEdgeId,
            kind,
            dto.Penalty,
            dto.Attributes ?? new Dictionary<string, string?>());
        error = string.Empty;
        return true;
    }

    private static bool TryParseRestrictionKind(string? kind, out NetworkTurnRestrictionKind parsed)
    {
        switch (kind)
        {
            case "prohibited":
                parsed = NetworkTurnRestrictionKind.Prohibited;
                return true;
            case "required":
                parsed = NetworkTurnRestrictionKind.Required;
                return true;
            case "penalty":
                parsed = NetworkTurnRestrictionKind.Penalty;
                return true;
            default:
                parsed = default;
                return false;
        }
    }

    private static NetworkTopologyGenerationDto ToDto(NetworkTopologyGeneration generation) => new()
    {
        DatasetId = generation.DatasetId,
        Generation = generation.Generation,
        SourceRevision = generation.SourceRevision,
        State = FormatState(generation.State),
        RowVersion = generation.RowVersion,
        Srid = generation.Srid,
        CreatedAt = generation.CreatedAt,
        UpdatedAt = generation.UpdatedAt,
        ActivatedAt = generation.ActivatedAt,
        FailureCode = generation.FailureCode,
    };

    private static NetworkTopologyEditResultDto ToDto(NetworkTopologyEditResult result) => new()
    {
        DatasetId = result.DatasetId,
        Generation = result.Generation,
        SourceRevision = result.SourceRevision,
        RowVersion = result.RowVersion,
        State = FormatState(result.State),
        EdgesAdded = result.EdgesAdded,
        EdgesUpdated = result.EdgesUpdated,
        EdgesDeleted = result.EdgesDeleted,
        RestrictionsAdded = result.RestrictionsAdded,
        RestrictionsUpdated = result.RestrictionsUpdated,
        RestrictionsDeleted = result.RestrictionsDeleted,
        WasIdempotentReplay = result.WasIdempotentReplay,
    };

    private static string FormatState(NetworkTopologyGenerationState state) => state switch
    {
        NetworkTopologyGenerationState.Draft => "draft",
        NetworkTopologyGenerationState.Dirty => "dirty",
        NetworkTopologyGenerationState.Building => "building",
        NetworkTopologyGenerationState.Ready => "ready",
        NetworkTopologyGenerationState.Active => "active",
        NetworkTopologyGenerationState.Failed => "failed",
        NetworkTopologyGenerationState.Retired => "retired",
        _ => "unknown",
    };

    private static string ResolveActor(HttpContext context)
        => context.User.Identity?.Name is { Length: > 0 } name ? name : "admin";

    /// <summary>
    /// Log category marker used by <see cref="ILogger{TCategoryName}"/> bindings.
    /// </summary>
    internal sealed class NetworkTopologyEditAdminLogCategory;

    /// <summary>
    /// Source-generated logger messages for the network-topology edit admin endpoints.
    /// Structured audit fields deliberately exclude geometry, attribute values, and
    /// profile costs (#2716 audit/telemetry requirement) — only dataset, generation,
    /// operation, counts, actor, correlation id, and outcome are recorded.
    /// </summary>
    internal static partial class NetworkTopologyEditAdminLog
    {
        [LoggerMessage(EventId = 4700, Level = LogLevel.Information,
            Message = "Allocated draft topology generation {Generation} for dataset '{DatasetId}'")]
        public static partial void AllocatedDraft(ILogger logger, string datasetId, long generation);

        [LoggerMessage(EventId = 4701, Level = LogLevel.Debug,
            Message = "Listed {Count} topology generations for dataset '{DatasetId}'")]
        public static partial void ListedGenerations(ILogger logger, string datasetId, int count);

        [LoggerMessage(EventId = 4702, Level = LogLevel.Information,
            Message = "Applied topology edit batch for dataset '{DatasetId}' generation {Generation}: " +
                      "actor={Actor} correlationId={CorrelationId} edgeItems={EdgeItemCount} restrictionItems={RestrictionItemCount} replay={WasReplay}")]
        public static partial void EditApplied(
            ILogger logger,
            string datasetId,
            long generation,
            string actor,
            string correlationId,
            int edgeItemCount,
            int restrictionItemCount,
            bool wasReplay);

        [LoggerMessage(EventId = 4703, Level = LogLevel.Warning,
            Message = "Topology edit batch validation failed for dataset '{DatasetId}' generation {Generation}: {Error}")]
        public static partial void ValidationFailed(ILogger logger, string datasetId, long generation, string error);

        [LoggerMessage(EventId = 4704, Level = LogLevel.Warning,
            Message = "Topology edit batch rejected for dataset '{DatasetId}' generation {Generation}: reason={Reason} actor={Actor}")]
        public static partial void ConflictRejected(ILogger logger, string datasetId, long generation, string reason, string actor);

        [LoggerMessage(EventId = 4705, Level = LogLevel.Error,
            Message = "Network topology edit admin operation '{Operation}' failed.")]
        public static partial void OperationFailed(ILogger logger, string operation, Exception exception);
    }
}
