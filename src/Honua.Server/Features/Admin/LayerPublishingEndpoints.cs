// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Exceptions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for publishing layers from PostGIS tables.
/// </summary>
internal static class LayerPublishingEndpoints
{
    private const int MaxSafeLayerPublishingMessageLength = 240;
    private const string LayerPublishingValidationMessage = "Layer publishing request is invalid.";
    private const string LayerPublishingConflictMessage = "Layer publishing operation conflicted with existing data.";
    private const string LayerPublishingNotFoundMessage = "The requested resource was not found.";

    internal sealed class LayerPublishingEndpointsLog;

    public static void MapLayerPublishingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/connections/{id}/layers")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Layers")
            .RequireAdminAuthorization();

        group.MapGet("/", HandleListLayers)
            .WithDisplayName("List Published Layers")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/", HandlePublishLayer)
            .WithDisplayName("Publish Layer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<ApiResponse<PublishedLayerSummary>>(StatusCodes.Status201Created)
            .Produces<ApiResponse<object>>(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .Produces<ApiResponse<object>>(StatusCodes.Status404NotFound)
            .Produces<ApiResponse<object>>(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapPut("/{layerId:int}/enabled", HandleSetLayerEnabled)
            .WithDisplayName("Set Layer Enabled")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapPut("/enabled", HandleSetServiceLayersEnabled)
            .WithDisplayName("Set Service Layers Enabled")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));
    }

    private static async Task<Results<Ok<ApiResponse<IReadOnlyList<PublishedLayerSummary>>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, ProblemHttpResult, ForbidHttpResult>>
        HandleListLayers(
            string id,
            string? serviceName,
            [FromServices] ISecureConnectionResolver resolver,
            [FromServices] ILayerPublishingService publishingService,
            HttpContext context,
            [FromServices] ILogger<LayerPublishingEndpointsLog> logger)
    {
        try
        {
            var connectionString = await ResolveConnectionStringAsync(id, resolver, context.RequestAborted);
            var layers = await publishingService.ListPublishedLayersAsync(
                connectionString,
                serviceName ?? "default",
                context.RequestAborted);

            return TypedResults.Ok(ApiResponse<IReadOnlyList<PublishedLayerSummary>>.CreateSuccess(layers));
        }
        catch (LayerPublishingException ex)
        {
            logger.LogWarning(ex, "Layer list failed: {Message}", ex.Message);
            return TypedResults.BadRequest(ApiResponse<object>.Failure(GetSafeLayerPublishingMessage(ex)));
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Layer list invalid request");
            return TypedResults.BadRequest(ApiResponse<object>.Failure("Invalid request parameters."));
        }
        catch (ResourceNotFoundException ex)
        {
            logger.LogWarning(ex, "Layer list connection not found");
            return TypedResults.NotFound(ApiResponse<object>.Failure("The requested resource was not found."));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Layer list failed due to invalid operation");
            return TypedResults.Problem(
                title: "Layer list failed",
                detail: "An internal error occurred while retrieving layers.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Layer list forbidden");
            return TypedResults.Forbid();
        }
    }

    private static async Task<IResult>
        HandlePublishLayer(
            string id,
            PublishLayerRequest request,
            [FromServices] ISecureConnectionResolver resolver,
            [FromServices] ILayerPublishingService publishingService,
            [FromServices] IDatabaseMigrationRunner migrationRunner,
            HttpContext context,
            [FromServices] ILogger<LayerPublishingEndpointsLog> logger)
    {
        var gate = context.RequestServices.GetRequiredService<OperatorApprovalGate>();
        var approvalResult = gate.EvaluateApproval(
            context, OperatorResourceType.Catalog, OperatorOperation.Publish);
        if (approvalResult != null) return approvalResult;

        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(request, new ValidationContext(request), validationResults, true))
        {
            var errors = string.Join(", ", validationResults.Select(r => r.ErrorMessage));
            return TypedResults.BadRequest(ApiResponse<object>.Failure($"Validation failed: {errors}"));
        }

        try
        {
            var connectionString = await ResolveConnectionStringAsync(id, resolver, context.RequestAborted);
            var migrationResult = await migrationRunner.RunMigrationsAsync(
                connectionString,
                typeof(Program).Assembly,
                context.RequestAborted);

            if (!migrationResult.Successful)
            {
                logger.LogError(migrationResult.Error, "Layer publish migration failed: {Message}", migrationResult.ErrorMessage);
                return TypedResults.BadRequest(ApiResponse<object>.Failure("Database migration failed."));
            }

            var connectionId = Guid.TryParse(id, out var parsedId) ? parsedId : (Guid?)null;
            var publishRequest = new LayerPublishRequest
            {
                Schema = request.Schema,
                Table = request.Table,
                LayerName = request.LayerName,
                Description = request.Description,
                GeometryColumn = request.GeometryColumn,
                GeometryType = request.GeometryType,
                Srid = request.Srid,
                PrimaryKey = request.PrimaryKey,
                Fields = request.Fields ?? Array.Empty<string>(),
                ServiceName = request.ServiceName,
                ConnectionId = connectionId,
                Enabled = request.Enabled
            };

            var result = await publishingService.PublishLayerAsync(
                connectionString,
                publishRequest,
                context.RequestAborted);

            await InvalidateServiceCatalogCacheAsync(
                context,
                result.ServiceName,
                [result.LayerId],
                logger).ConfigureAwait(false);

            var location = $"/api/v1/admin/connections/{id}/layers/{result.LayerId}";
            return TypedResults.Created(location, ApiResponse<PublishedLayerSummary>.CreateSuccess(result));
        }
        catch (LayerPublishingException ex) when (ex.ErrorKind == LayerPublishingErrorKind.Conflict)
        {
            logger.LogWarning(ex, "Layer publish conflict");
            return TypedResults.Conflict(ApiResponse<object>.Failure(GetSafeLayerPublishingMessage(ex)));
        }
        catch (LayerPublishingException ex) when (ex.ErrorKind == LayerPublishingErrorKind.NotFound)
        {
            logger.LogWarning(ex, "Layer publish not found");
            return TypedResults.NotFound(ApiResponse<object>.Failure(GetSafeLayerPublishingMessage(ex)));
        }
        catch (LayerPublishingException ex)
        {
            logger.LogWarning(ex, "Layer publish validation failed");
            return TypedResults.BadRequest(ApiResponse<object>.Failure(GetSafeLayerPublishingMessage(ex)));
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Layer publish invalid request");
            return TypedResults.BadRequest(ApiResponse<object>.Failure("Invalid request parameters."));
        }
        catch (ResourceNotFoundException ex)
        {
            logger.LogWarning(ex, "Layer publish connection not found");
            return TypedResults.NotFound(ApiResponse<object>.Failure("The requested resource was not found."));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Layer publish failed due to invalid operation");
            return TypedResults.Problem(
                title: "Layer publish failed",
                detail: "An internal error occurred while publishing the layer.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
        catch (UnauthorizedAccessException)
        {
            return TypedResults.Forbid();
        }
    }

    private static async Task<Results<Ok<ApiResponse<PublishedLayerSummary>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, ProblemHttpResult, ForbidHttpResult>>
        HandleSetLayerEnabled(
            string id,
            int layerId,
            LayerEnabledRequest request,
            string? serviceName,
            [FromServices] ISecureConnectionResolver resolver,
            [FromServices] ILayerPublishingService publishingService,
            [FromServices] IDatabaseMigrationRunner migrationRunner,
            HttpContext context,
            [FromServices] ILogger<LayerPublishingEndpointsLog> logger)
    {
        try
        {
            var connectionString = await ResolveConnectionStringAsync(id, resolver, context.RequestAborted);
            var migrationResult = await migrationRunner.RunMigrationsAsync(
                connectionString,
                typeof(Program).Assembly,
                context.RequestAborted);

            if (!migrationResult.Successful)
            {
                logger.LogError(migrationResult.Error, "Layer enable migration failed: {Message}", migrationResult.ErrorMessage);
                return TypedResults.BadRequest(ApiResponse<object>.Failure("Database migration failed."));
            }

            var result = await publishingService.SetLayerEnabledAsync(
                connectionString,
                layerId,
                serviceName ?? "default",
                request.Enabled,
                context.RequestAborted);

            if (result == null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure("Layer not found."));
            }

            await InvalidateServiceCatalogCacheAsync(
                context,
                result.ServiceName,
                [result.LayerId],
                logger).ConfigureAwait(false);

            return TypedResults.Ok(ApiResponse<PublishedLayerSummary>.CreateSuccess(result));
        }
        catch (LayerPublishingException ex)
        {
            logger.LogWarning(ex, "Layer toggle failed");
            return TypedResults.BadRequest(ApiResponse<object>.Failure(GetSafeLayerPublishingMessage(ex)));
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Layer toggle invalid request");
            return TypedResults.BadRequest(ApiResponse<object>.Failure("Invalid request parameters."));
        }
        catch (ResourceNotFoundException ex)
        {
            logger.LogWarning(ex, "Layer toggle connection not found");
            return TypedResults.NotFound(ApiResponse<object>.Failure("The requested resource was not found."));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Layer toggle failed due to invalid operation");
            return TypedResults.Problem(
                title: "Layer toggle failed",
                detail: "An internal error occurred while updating layer state.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
        catch (UnauthorizedAccessException)
        {
            return TypedResults.Forbid();
        }
    }

    private static async Task<Results<Ok<ApiResponse<IReadOnlyList<PublishedLayerSummary>>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, ProblemHttpResult, ForbidHttpResult>>
        HandleSetServiceLayersEnabled(
            string id,
            LayerEnabledRequest request,
            string? serviceName,
            [FromServices] ISecureConnectionResolver resolver,
            [FromServices] ILayerPublishingService publishingService,
            [FromServices] IDatabaseMigrationRunner migrationRunner,
            HttpContext context,
            [FromServices] ILogger<LayerPublishingEndpointsLog> logger)
    {
        try
        {
            var connectionString = await ResolveConnectionStringAsync(id, resolver, context.RequestAborted);
            var migrationResult = await migrationRunner.RunMigrationsAsync(
                connectionString,
                typeof(Program).Assembly,
                context.RequestAborted);

            if (!migrationResult.Successful)
            {
                logger.LogError(migrationResult.Error, "Layer bulk enable migration failed: {Message}", migrationResult.ErrorMessage);
                return TypedResults.BadRequest(ApiResponse<object>.Failure("Database migration failed."));
            }

            var result = await publishingService.SetServiceLayersEnabledAsync(
                connectionString,
                serviceName ?? "default",
                request.Enabled,
                context.RequestAborted);

            var cacheServiceName = !string.IsNullOrWhiteSpace(serviceName)
                ? serviceName
                : result.Count > 0 ? result[0].ServiceName : null;
            await InvalidateServiceCatalogCacheAsync(
                context,
                cacheServiceName,
                result.Select(layer => layer.LayerId),
                logger).ConfigureAwait(false);

            return TypedResults.Ok(ApiResponse<IReadOnlyList<PublishedLayerSummary>>.CreateSuccess(result));
        }
        catch (LayerPublishingException ex)
        {
            logger.LogWarning(ex, "Layer bulk toggle failed");
            return TypedResults.BadRequest(ApiResponse<object>.Failure(GetSafeLayerPublishingMessage(ex)));
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Layer bulk toggle invalid request");
            return TypedResults.BadRequest(ApiResponse<object>.Failure("Invalid request parameters."));
        }
        catch (ResourceNotFoundException ex)
        {
            logger.LogWarning(ex, "Layer bulk toggle connection not found");
            return TypedResults.NotFound(ApiResponse<object>.Failure("The requested resource was not found."));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Layer bulk toggle failed due to invalid operation");
            return TypedResults.Problem(
                title: "Layer bulk toggle failed",
                detail: "An internal error occurred while updating service layer state.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
        catch (UnauthorizedAccessException)
        {
            return TypedResults.Forbid();
        }
    }

    private static async Task<string> ResolveConnectionStringAsync(
        string id,
        ISecureConnectionResolver resolver,
        CancellationToken cancellationToken)
    {
        if (Guid.TryParse(id, out var connectionId))
        {
            return await resolver.ResolveConnectionStringAsync(connectionId, cancellationToken);
        }

        return await resolver.ResolveConnectionStringAsync(id, cancellationToken);
    }

    private static async Task InvalidateServiceCatalogCacheAsync(
        HttpContext context,
        string? serviceName,
        IEnumerable<int> layerIds,
        ILogger<LayerPublishingEndpointsLog> logger)
    {
        var distinctLayerIds = layerIds
            .Where(layerId => layerId > 0)
            .Distinct()
            .ToArray();

        var featureCacheManager = context.RequestServices.GetService<IFeatureCacheManager>();
        if (featureCacheManager != null)
        {
            foreach (var layerId in distinctLayerIds)
            {
                featureCacheManager.InvalidateLayerCache(layerId);
            }
        }

        var cacheInvalidator = context.RequestServices.GetService<OutputCacheInvalidationService>();
        if (cacheInvalidator == null)
        {
            return;
        }

        try
        {
            await cacheInvalidator.InvalidateServiceCatalogAsync(
                serviceName,
                distinctLayerIds,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to invalidate service catalog cache for {ServiceName}", serviceName);
        }
    }

    private static string GetSafeLayerPublishingMessage(LayerPublishingException exception)
    {
        var fallback = exception.ErrorKind switch
        {
            LayerPublishingErrorKind.Conflict => LayerPublishingConflictMessage,
            LayerPublishingErrorKind.NotFound => LayerPublishingNotFoundMessage,
            _ => LayerPublishingValidationMessage
        };

        return SanitizeLayerPublishingMessage(exception.Message, fallback);
    }

    private static string SanitizeLayerPublishingMessage(string? message, string fallback)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return fallback;
        }

        var trimmed = message.Trim();
        if (trimmed.Length > MaxSafeLayerPublishingMessageLength || ContainsUnsafeMessagePattern(trimmed))
        {
            return fallback;
        }

        return trimmed;
    }

    private static bool ContainsUnsafeMessagePattern(string message)
    {
        return message.Contains('\r') ||
               message.Contains('\n') ||
               message.Contains("BytePositionInLine", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("LineNumber", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("System.", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("StackTrace", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("SQLSTATE", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("password", StringComparison.OrdinalIgnoreCase);
    }
}
