// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using PermanentFilterLanguages = Honua.Core.Features.Metadata.Domain.V2.MetadataV2PermanentFilterLanguages;

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
        if (layerResult.Problem != null || layerResult.Resource == null)
        {
            return layerResult.Problem!;
        }

        var response = BuildResponse(layerId, layerResult.Resource.PermanentFilter);
        return Results.Json(
            ApiResponse<LayerFilterConfigurationResponse>.CreateSuccess(response),
            LayerFieldConfigurationJsonContext.Default.ApiResponseLayerFilterConfigurationResponse);
    }

    private static async Task<IResult> HandleUpdateLayerFilter(
        int layerId,
        LayerFilterConfigurationUpdateRequest request,
        HttpContext context,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] IMetadataV2GraphStore graphStore,
        [FromServices] IFilterExpressionService filterExpressionService,
        [FromServices] OutputCacheInvalidationService cacheInvalidator,
        CancellationToken cancellationToken)
    {
        var layerResult = await ValidateLayerAsync(layerId, context, resourceValidator, cancellationToken).ConfigureAwait(false);
        if (layerResult.Problem != null || layerResult.Resource == null)
        {
            return layerResult.Problem!;
        }

        var validationResult = ValidateAndBuildPermanentFilter(
            layerResult.Resource,
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

        await WriteResourcePermanentFilterAsync(
            graphStore,
            layerId,
            validationResult.Filter,
            cancellationToken).ConfigureAwait(false);
        await cacheInvalidator.InvalidateServiceCatalogAsync(null, [layerId], cancellationToken).ConfigureAwait(false);

        var response = BuildResponse(layerId, validationResult.Filter);
        return Results.Json(
            ApiResponse<LayerFilterConfigurationResponse>.CreateSuccess(response),
            LayerFieldConfigurationJsonContext.Default.ApiResponseLayerFilterConfigurationResponse);
    }

    /// <summary>
    /// Writes the per-layer permanent filter onto every V2 resource that this layer id
    /// publishes through. Slice 51/N moved <c>LayerMetadata.PermanentFilter</c> to
    /// <see cref="MetadataV2Resource.PermanentFilter"/>; this endpoint is the migrated
    /// admin write-path. Passing <c>null</c> clears the filter.
    /// </summary>
    private static async Task WriteResourcePermanentFilterAsync(
        IMetadataV2GraphStore graphStore,
        int layerId,
        MetadataV2PermanentFilter? filter,
        CancellationToken cancellationToken)
    {
        var snapshot = await graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        // Resolve every resource id that this layer id publishes through. The admin
        // permanent-filter endpoint is service-agnostic (it operates on layer ids only),
        // so we walk all publications whose numeric identifier equals layerId and apply
        // the change to each unique backing resource.
        var targetResourceIds = snapshot.Graph.Publications
            .Where(p => p.Identifier.IsNumeric && p.LayerIndex == layerId)
            .Select(p => p.ResourceId)
            .ToHashSet(StringComparer.Ordinal);

        if (targetResourceIds.Count == 0)
        {
            return;
        }

        var resources = snapshot.Graph.Resources.ToArray();
        for (var i = 0; i < resources.Length; i++)
        {
            if (targetResourceIds.Contains(resources[i].Metadata.Id))
            {
                resources[i] = resources[i] with { PermanentFilter = filter };
            }
        }

        var updated = snapshot.Graph with
        {
            Resources = resources,
            Revision = snapshot.Graph.Revision + 1,
        };
        _ = await graphStore.SaveAsync(updated, snapshot.Etag, cancellationToken).ConfigureAwait(false);
    }

    private static (MetadataV2PermanentFilter? Filter, string? Error) ValidateAndBuildPermanentFilter(
        MetadataV2Resource resource,
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

        // V2 cutover: validate the candidate filter against the resource's V2 schema fields
        // via ParseAndNormalize + Translate(expression, resource). Field types come from
        // MetadataV2Resource.SchemaFields, which is the canonical post-cutover schema source.
        var parseResult = filterExpressionService.ParseAndNormalize(filterLanguage, expression, resource);
        if (!parseResult.IsSuccess)
        {
            return (null, $"Permanent filter is invalid: {parseResult.ErrorMessage ?? "Invalid filter."}");
        }

        var translationResult = filterExpressionService.Translate(parseResult.Expression, resource);
        if (!translationResult.IsSuccess)
        {
            return (null, $"Permanent filter is invalid: {translationResult.ErrorMessage ?? "Invalid filter."}");
        }

        return (new MetadataV2PermanentFilter
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
        canonicalLanguage = PermanentFilterLanguages.ArcGisSql;
        filterLanguage = FilterLanguage.ArcGisSql;
        var normalized = (language ?? PermanentFilterLanguages.ArcGisSql)
            .Trim()
            .ToLowerInvariant();

        switch (normalized)
        {
            case PermanentFilterLanguages.ArcGisSql:
            case "arcgis":
            case "geoservices-sql":
                canonicalLanguage = PermanentFilterLanguages.ArcGisSql;
                filterLanguage = FilterLanguage.ArcGisSql;
                return true;
            case PermanentFilterLanguages.Cql2Text:
            case "cql2":
                canonicalLanguage = PermanentFilterLanguages.Cql2Text;
                filterLanguage = FilterLanguage.Cql2Text;
                return true;
            case PermanentFilterLanguages.Cql2Json:
                canonicalLanguage = PermanentFilterLanguages.Cql2Json;
                filterLanguage = FilterLanguage.Cql2Json;
                return true;
            default:
                return false;
        }
    }

    private static async Task<(MetadataV2Resource? Resource, IResult? Problem)> ValidateLayerAsync(
        int layerId,
        HttpContext context,
        IResourceValidator resourceValidator,
        CancellationToken cancellationToken)
    {
        var layerResult = await resourceValidator.ValidateLayerV2Async(layerId, cancellationToken).ConfigureAwait(false);
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

    private static LayerFilterConfigurationResponse BuildResponse(int layerId, MetadataV2PermanentFilter? filter)
        => new()
        {
            LayerId = layerId,
            PermanentFilter = BuildPermanentFilterConfiguration(filter)
        };

    private static LayerPermanentFilterConfiguration? BuildPermanentFilterConfiguration(MetadataV2PermanentFilter? filter)
        => filter == null
            ? null
            : new LayerPermanentFilterConfiguration
            {
                Expression = filter.Expression,
                Language = filter.Language
            };
}
