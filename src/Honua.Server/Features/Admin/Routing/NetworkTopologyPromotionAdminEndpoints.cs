// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Admin.Routing;

/// <summary>
/// Admin promotion/rollback surface for the active network-topology generation (#2719).
/// Every mutation runs through the canonical <see cref="INetworkTopologyPromotionStore"/>
/// so validation, evidence/precondition checks, the atomic active-pointer flip, and
/// immutable history stay consistent across the promote and rollback paths.
/// </summary>
internal static partial class NetworkTopologyPromotionAdminEndpoints
{
    private const string TagAdmin = "Admin";
    private const string TagNetworkDatasets = "Network Datasets";
    private const string IdempotencyKeyHeader = "Idempotency-Key";
    private const int MaxIdempotencyKeyLength = 200;

    /// <summary>
    /// Maps the network-topology promotion admin endpoints into the request pipeline.
    /// </summary>
    /// <remarks>
    /// No-op when <see cref="INetworkTopologyPromotionStore"/> is not registered
    /// (non-Postgres data providers), mirroring the other network-topology admin surfaces.
    /// </remarks>
    public static IEndpointRouteBuilder MapNetworkTopologyPromotionAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var serviceInspector = endpoints.ServiceProvider.GetService<IServiceProviderIsService>();
        if (serviceInspector is not null && !serviceInspector.IsService(typeof(INetworkTopologyPromotionStore)))
        {
            return endpoints;
        }

        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/network-datasets/{id}")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags(TagAdmin, TagNetworkDatasets)
            .RequireAdminAuthorization();

        _ = group.MapPost("/promote", HandlePromote)
            .WithName("PromoteNetworkTopologyGeneration")
            .WithSummary("Atomically promote a ready topology generation to active.")
            .Accepts<NetworkTopologyPromotionRequest>("application/json")
            .Produces<NetworkTopologyPromotionDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        _ = group.MapPost("/rollback", HandleRollback)
            .WithName("RollbackNetworkTopologyGeneration")
            .WithSummary("Atomically roll back to a previously active, retired topology generation.")
            .Accepts<NetworkTopologyPromotionRequest>("application/json")
            .Produces<NetworkTopologyPromotionDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        _ = group.MapGet("/promotions", HandleListHistory)
            .WithName("ListNetworkTopologyPromotions")
            .WithSummary("List a dataset's promotion/rollback history.")
            .Produces<NetworkTopologyPromotionDto[]>(StatusCodes.Status200OK);

        return endpoints;
    }

    private static async Task<IResult> HandlePromote(
        string id,
        HttpContext context,
        [FromBody] NetworkTopologyPromotionRequest request,
        [FromServices] INetworkTopologyPromotionStore promotionStore,
        [FromServices] ILogger<NetworkTopologyPromotionAdminLogCategory> logger,
        CancellationToken cancellationToken)
        => await ApplyAsync(
                id, context, request, promotionStore, logger,
                (store, datasetId, target, activeGen, activeRowVersion, actor, reason, key, ct)
                    => store.PromoteAsync(datasetId, target, activeGen, activeRowVersion, actor, reason, key, ct),
                cancellationToken)
            .ConfigureAwait(false);

    private static async Task<IResult> HandleRollback(
        string id,
        HttpContext context,
        [FromBody] NetworkTopologyPromotionRequest request,
        [FromServices] INetworkTopologyPromotionStore promotionStore,
        [FromServices] ILogger<NetworkTopologyPromotionAdminLogCategory> logger,
        CancellationToken cancellationToken)
        => await ApplyAsync(
                id, context, request, promotionStore, logger,
                (store, datasetId, target, activeGen, activeRowVersion, actor, reason, key, ct)
                    => store.RollbackAsync(datasetId, target, activeGen, activeRowVersion, actor, reason, key, ct),
                cancellationToken)
            .ConfigureAwait(false);

    private static async Task<IResult> ApplyAsync(
        string id,
        HttpContext context,
        NetworkTopologyPromotionRequest request,
        INetworkTopologyPromotionStore promotionStore,
        ILogger<NetworkTopologyPromotionAdminLogCategory> logger,
        Func<INetworkTopologyPromotionStore, string, long, long, long, string, string?, string, CancellationToken, Task<NetworkTopologyPromotionRecord>> operation,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, "Request body is required.");
        }

        if (!TryResolveIdempotencyKey(context, out var idempotencyKey, out var headerError))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, headerError);
        }

        if (request.TargetGeneration is not { } targetGeneration || targetGeneration <= 0)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, "targetGeneration is required and must be positive.");
        }

        if (request.ExpectedActiveGeneration is not { } expectedActiveGeneration || expectedActiveGeneration <= 0)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, "expectedActiveGeneration is required and must be positive.");
        }

        if (request.ExpectedActiveRowVersion is not { } expectedActiveRowVersion || expectedActiveRowVersion <= 0)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, "expectedActiveRowVersion is required and must be positive.");
        }

        var actor = ResolveActor(context);
        try
        {
            var record = await operation(
                    promotionStore, id, targetGeneration, expectedActiveGeneration, expectedActiveRowVersion,
                    actor, request.Reason, idempotencyKey, cancellationToken)
                .ConfigureAwait(false);

            NetworkTopologyPromotionAdminLog.PromotionApplied(logger, id, record.FromGeneration ?? 0, record.ToGeneration, record.Kind, actor);
            return Results.Json(ToDto(record), NetworkTopologyRebuildAdminJsonContext.Default.NetworkTopologyPromotionDto);
        }
        catch (NetworkTopologyPromotionConflictException ex)
        {
            var status = ex.Reason is NetworkTopologyPromotionRejection.ActiveGenerationNotFound or NetworkTopologyPromotionRejection.CandidateNotFound
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status409Conflict;
            NetworkTopologyPromotionAdminLog.PromotionRejected(logger, id, ex.Reason, actor);
            return ProblemDetailsHelpers.CreateAdminProblem(context, status, ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            NetworkTopologyPromotionAdminLog.OperationFailed(logger, "promotion.apply", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to apply the promotion.");
        }
    }

    private static async Task<IResult> HandleListHistory(
        string id,
        HttpContext context,
        [FromServices] INetworkTopologyPromotionStore promotionStore,
        [FromServices] ILogger<NetworkTopologyPromotionAdminLogCategory> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var history = await promotionStore.ListHistoryAsync(id, cancellationToken).ConfigureAwait(false);
            var dtos = history.Select(ToDto).ToArray();
            return Results.Json(dtos, NetworkTopologyRebuildAdminJsonContext.Default.NetworkTopologyPromotionDtoArray);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            NetworkTopologyPromotionAdminLog.OperationFailed(logger, "promotion.list-history", ex);
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError,
                "Failed to list promotion history.");
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

    private static NetworkTopologyPromotionDto ToDto(NetworkTopologyPromotionRecord record) => new()
    {
        PromotionId = record.PromotionId,
        DatasetId = record.DatasetId,
        FromGeneration = record.FromGeneration,
        ToGeneration = record.ToGeneration,
        Kind = record.Kind == NetworkTopologyPromotionKind.Promote ? "promote" : "rollback",
        Actor = record.Actor,
        Reason = record.Reason,
        EvidenceDigest = record.EvidenceDigest,
        PromotedAt = record.PromotedAt,
    };

    private static string ResolveActor(HttpContext context)
        => context.User.Identity?.Name is { Length: > 0 } name ? name : "admin";

    /// <summary>
    /// Log category marker used by <see cref="ILogger{TCategoryName}"/> bindings.
    /// </summary>
    internal sealed class NetworkTopologyPromotionAdminLogCategory;

    /// <summary>
    /// Source-generated logger messages for the network-topology promotion admin endpoints.
    /// </summary>
    internal static partial class NetworkTopologyPromotionAdminLog
    {
        [LoggerMessage(EventId = 4810, Level = LogLevel.Information,
            Message = "Applied topology promotion for dataset '{DatasetId}': {FromGeneration} -> {ToGeneration} ({Kind}), actor={Actor}")]
        public static partial void PromotionApplied(ILogger logger, string datasetId, long fromGeneration, long toGeneration, NetworkTopologyPromotionKind kind, string actor);

        [LoggerMessage(EventId = 4811, Level = LogLevel.Warning,
            Message = "Topology promotion rejected for dataset '{DatasetId}': reason={Reason} actor={Actor}")]
        public static partial void PromotionRejected(ILogger logger, string datasetId, NetworkTopologyPromotionRejection reason, string actor);

        [LoggerMessage(EventId = 4812, Level = LogLevel.Error,
            Message = "Network topology promotion admin operation '{Operation}' failed.")]
        public static partial void OperationFailed(ILogger logger, string operation, Exception exception);
    }
}
