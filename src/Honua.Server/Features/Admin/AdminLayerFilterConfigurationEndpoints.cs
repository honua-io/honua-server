// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for persisted layer permanent filter configuration.
/// </summary>
internal static class AdminLayerFilterConfigurationEndpoints
{
    private const int MaxFilterExpressionLength = 4096;

    /// <summary>
    /// Maps layer permanent filter configuration endpoints to the admin metadata API group.
    /// </summary>
    public static void MapAdminLayerFilterConfigurationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/metadata/layers")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Metadata", "Filters")
            .RequireAdminAuthorization();

        _ = group.MapGet("/{layerId:int}/filter", HandleGetLayerFilter)
            .WithName("GetAdminLayerFilter")
            .WithSummary("Get persisted layer permanent filter configuration")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.MapPut("/{layerId:int}/filter", HandleUpdateLayerFilter)
            .WithName("UpdateAdminLayerFilter")
            .WithSummary("Update persisted layer permanent filter configuration")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));
    }

    private static async Task<IResult> HandleGetLayerFilter(
        int layerId,
        HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        CancellationToken cancellationToken)
    {
        var layerResult = await ValidateLayerAsync(layerId, context, resourceValidator, cancellationToken).ConfigureAwait(false);
        if (layerResult.Problem != null || layerResult.Layer == null)
        {
            return layerResult.Problem!;
        }

        var response = BuildResponse(layerResult.Layer);
        return Results.Json(
            ApiResponse<LayerFilterConfigurationResponse>.CreateSuccess(response),
            LayerFieldConfigurationJsonContext.Default.ApiResponseLayerFilterConfigurationResponse);
    }

    private static async Task<IResult> HandleUpdateLayerFilter(
        int layerId,
        LayerFilterConfigurationUpdateRequest request,
        HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] ILayerMetadataUpdater layerMetadataUpdater,
        [FromServices] IFilterExpressionService filterExpressionService,
        [FromServices] OutputCacheInvalidationService cacheInvalidator,
        CancellationToken cancellationToken)
    {
        var layerResult = await ValidateLayerAsync(layerId, context, resourceValidator, cancellationToken).ConfigureAwait(false);
        if (layerResult.Problem != null || layerResult.Layer == null)
        {
            return layerResult.Problem!;
        }

        var validationResult = ValidateAndBuildPermanentFilter(
            layerResult.Layer,
            request.PermanentFilter,
            filterExpressionService);
        if (validationResult.Error != null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                "Invalid layer filter",
                validationResult.Error);
        }

        var metadata = (layerResult.Layer.Metadata ?? new CatalogMetadata()) with
        {
            PermanentFilter = validationResult.Filter
        };

        await layerMetadataUpdater.UpdateLayerMetadataAsync(layerId, metadata, cancellationToken).ConfigureAwait(false);
        await cacheInvalidator.InvalidateServiceCatalogAsync(null, [layerId], cancellationToken).ConfigureAwait(false);

        var response = BuildResponse(layerResult.Layer with { Metadata = metadata });
        return Results.Json(
            ApiResponse<LayerFilterConfigurationResponse>.CreateSuccess(response),
            LayerFieldConfigurationJsonContext.Default.ApiResponseLayerFilterConfigurationResponse);
    }

    private static (LayerPermanentFilter? Filter, string? Error) ValidateAndBuildPermanentFilter(
        LayerDefinition layer,
        LayerPermanentFilterConfiguration? configuration,
        IFilterExpressionService filterExpressionService)
    {
        if (configuration == null)
        {
            return (null, null);
        }

        var expression = configuration.Expression?.Trim();
        if (string.IsNullOrWhiteSpace(expression))
        {
            return (null, "Permanent filter expression is required. Send null permanentFilter to clear the saved filter.");
        }

        if (expression.Length > MaxFilterExpressionLength)
        {
            return (null, $"Permanent filter expression must be {MaxFilterExpressionLength} characters or fewer.");
        }

        if (!TryNormalizeFilterLanguage(configuration.Language, out var canonicalLanguage, out var filterLanguage))
        {
            return (null, $"Unsupported permanent filter language '{configuration.Language}'. Supported values are arcgis-sql, cql2-text, and cql2-json.");
        }

        var translationResult = filterExpressionService.Translate(filterLanguage, expression, layer);
        if (!translationResult.IsSuccess)
        {
            return (null, $"Permanent filter is invalid: {translationResult.ErrorMessage ?? "Invalid filter."}");
        }

        return (new LayerPermanentFilter
        {
            Expression = expression,
            Language = canonicalLanguage
        }, null);
    }

    private static bool TryNormalizeFilterLanguage(
        string? language,
        out string canonicalLanguage,
        out FilterLanguage filterLanguage)
    {
        canonicalLanguage = LayerPermanentFilterLanguages.ArcGisSql;
        filterLanguage = FilterLanguage.ArcGisSql;
        var normalized = (language ?? LayerPermanentFilterLanguages.ArcGisSql)
            .Trim()
            .ToLowerInvariant();

        switch (normalized)
        {
            case LayerPermanentFilterLanguages.ArcGisSql:
            case "arcgis":
            case "geoservices-sql":
                canonicalLanguage = LayerPermanentFilterLanguages.ArcGisSql;
                filterLanguage = FilterLanguage.ArcGisSql;
                return true;
            case LayerPermanentFilterLanguages.Cql2Text:
            case "cql2":
                canonicalLanguage = LayerPermanentFilterLanguages.Cql2Text;
                filterLanguage = FilterLanguage.Cql2Text;
                return true;
            case LayerPermanentFilterLanguages.Cql2Json:
                canonicalLanguage = LayerPermanentFilterLanguages.Cql2Json;
                filterLanguage = FilterLanguage.Cql2Json;
                return true;
            default:
                return false;
        }
    }

    private static async Task<(LayerDefinition? Layer, IResult? Problem)> ValidateLayerAsync(
        int layerId,
        HttpContext context,
        IResourceValidator resourceValidator,
        CancellationToken cancellationToken)
    {
        var layerResult = await resourceValidator.ValidateLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (!layerResult.IsValid || layerResult.Resource == null)
        {
            var statusCode = layerResult.ErrorCode == ResourceValidationError.InvalidIdentifier
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status404NotFound;
            var message = layerResult.ErrorMessage ?? $"Layer {layerId} not found.";
            return (null, ProblemDetailsHelpers.CreateAdminProblem(context, statusCode, message));
        }

        return (layerResult.Resource, null);
    }

    private static LayerFilterConfigurationResponse BuildResponse(LayerDefinition layer)
        => new()
        {
            LayerId = layer.Id,
            PermanentFilter = BuildPermanentFilterConfiguration(layer.Metadata?.PermanentFilter)
        };

    private static LayerPermanentFilterConfiguration? BuildPermanentFilterConfiguration(LayerPermanentFilter? filter)
        => filter == null
            ? null
            : new LayerPermanentFilterConfiguration
            {
                Expression = filter.Expression,
                Language = filter.Language
            };
}
