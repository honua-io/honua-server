// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcFeatures.Models;
using Honua.Server.Features.OgcFeatures.Services;

namespace Honua.Server.Features.OgcFeatures;

/// <summary>
/// Handler for OGC Features CRUD operations (Create, Update, Delete).
/// </summary>
internal sealed partial class OgcFeaturesCrudHandler(
    OgcFeaturesCrudDependencies dependencies,
    ILogger<OgcFeaturesCrudHandler> logger)
{
    private readonly IFeatureWriter _featureWriter = dependencies?.FeatureWriter ?? throw new ArgumentNullException(nameof(dependencies));
    private readonly ICrsRegistry _crsRegistry = dependencies.CrsRegistry;
    private readonly OgcFeaturesGeometryServices _geometryServices = dependencies.GeometryServices;
    private readonly FeatureMutationValidator _mutationValidator = dependencies.MutationValidator;
    private readonly ILogger<OgcFeaturesCrudHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Handles feature creation requests.
    /// </summary>
    public async Task<IResult> HandleCreateFeatureAsync(
        string collectionId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var layerValidation = await LayerValidationHelpers.ValidateCollectionWithAccessAsync(
                context, collectionId, scope: AccessScope.Write, cancellationToken: cancellationToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }
            var layer = layerValidation.Layer!;
            var layerId = layer.Id;

            var (requestFeature, requestError) = await OgcFeaturePayloadReader.ReadGeoJsonFeatureAsync(context, cancellationToken);
            if (requestFeature == null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, requestError ?? "Invalid GeoJSON payload.");
            }

            var buildResult = await OgcFeatureMutationHelpers.TryBuildFeatureAsync(
                context.Request,
                layer,
                requestFeature,
                _crsRegistry,
                _geometryServices,
                _mutationValidator,
                objectId: 0,
                cancellationToken);
            if (!buildResult.IsValid)
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    buildResult.ErrorMessage ?? "Invalid feature payload.");
            }

            var inputCrs = buildResult.InputCrs!.Value;
            var feature = buildResult.Feature
                ?? throw new InvalidOperationException("Feature build result was missing the feature payload.");

            var created = await _featureWriter.CreateAsync(layerId, feature, cancellationToken);
            await InvalidateCacheAsync(context, layerId, cancellationToken);
            var createLinks = BuildFeatureLinks(
                context.Request,
                collectionId,
                FormattableString.Invariant($"{created.Id}"),
                MediaTypes.GeoJson);
            context.Response.Headers["Content-Crs"] = $"<{inputCrs.Uri}>";
            var response = ToOgcFeature(created, inputCrs.AxisOrder, createLinks);

            return Results.Json(response, OgcJsonContext.Default.GeoJsonFeature, contentType: MediaTypes.GeoJson, statusCode: StatusCodes.Status201Created);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ResourceConflictException ex)
        {
            return StandardErrorHelpers.CreateConflict(context, ex.Message);
        }
        catch (Exception ex)
        {
            Log.CreateFeatureFailed(_logger, collectionId, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while creating the feature.");
        }
    }

    /// <summary>
    /// Handles feature update requests.
    /// </summary>
    public async Task<IResult> HandleUpdateFeatureAsync(
        string collectionId,
        string featureId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var layerValidation = await LayerValidationHelpers.ValidateCollectionWithAccessAsync(
                context, collectionId, scope: AccessScope.Write, cancellationToken: cancellationToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }
            var layer = layerValidation.Layer!;
            var layerId = layer.Id;

            if (!long.TryParse(featureId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectId))
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            var (requestFeature, requestError) = await OgcFeaturePayloadReader.ReadGeoJsonFeatureAsync(context, cancellationToken);
            if (requestFeature == null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, requestError ?? "Invalid GeoJSON payload.");
            }

            var buildResult = await OgcFeatureMutationHelpers.TryBuildFeatureAsync(
                context.Request,
                layer,
                requestFeature,
                _crsRegistry,
                _geometryServices,
                _mutationValidator,
                objectId,
                cancellationToken);
            if (!buildResult.IsValid)
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    buildResult.ErrorMessage ?? "Invalid feature payload.");
            }

            var inputCrs = buildResult.InputCrs!.Value;
            var feature = buildResult.Feature
                ?? throw new InvalidOperationException("Feature build result was missing the feature payload.");

            Feature updated;
            try
            {
                updated = await _featureWriter.UpdateAsync(layerId, feature, cancellationToken);
                await InvalidateCacheAsync(context, layerId, cancellationToken);
            }
            catch (ResourceConflictException ex)
            {
                return StandardErrorHelpers.CreateConflict(context, ex.Message);
            }
            catch (ResourceNotFoundException)
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }
            catch (InvalidOperationException)
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            var updateLinks = BuildFeatureLinks(
                context.Request,
                collectionId,
                FormattableString.Invariant($"{updated.Id}"),
                MediaTypes.GeoJson);
            context.Response.Headers["Content-Crs"] = $"<{inputCrs.Uri}>";
            var response = ToOgcFeature(updated, inputCrs.AxisOrder, updateLinks);
            return Results.Json(response, OgcJsonContext.Default.GeoJsonFeature, contentType: MediaTypes.GeoJson);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.UpdateFeatureFailed(_logger, collectionId, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while updating the feature.");
        }
    }

    /// <summary>
    /// Handles feature deletion requests.
    /// </summary>
    public async Task<IResult> HandleDeleteFeatureAsync(
        string collectionId,
        string featureId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var layerValidation = await LayerValidationHelpers.ValidateCollectionWithAccessAsync(
                context, collectionId, scope: AccessScope.Write, cancellationToken: cancellationToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }
            var layer = layerValidation.Layer!;
            var layerId = layer.Id;

            if (!long.TryParse(featureId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectId))
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            var deleted = await _featureWriter.DeleteAsync(layerId, objectId, cancellationToken);
            if (!deleted)
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            await InvalidateCacheAsync(context, layerId, cancellationToken);
            return Results.NoContent();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.DeleteFeatureFailed(_logger, collectionId, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while deleting the feature.");
        }
    }

    private GeoJsonFeature ToOgcFeature(
        Feature feature,
        AxisOrder axisOrder,
        ImmutableArray<Link>? links = null)
    {
        var geometry = _geometryServices.ConvertWkbToSimpleGeometry(feature.Geometry, axisOrder);
        return feature.ToGeoJsonBase().ToOgcGeoJsonFeature(geometry, links);
    }

    private static ImmutableArray<Link> BuildFeatureLinks(
        HttpRequest request,
        string collectionId,
        string featureId,
        string outputFormat)
    {
        var baseUrl = $"{request.Scheme}://{request.Host}";
        var basePath = $"{baseUrl}/ogc/features/collections/{collectionId}/items/{featureId}";

        var links = new List<Link>
        {
            Link.Create(
                href: basePath,
                rel: RelationTypes.Self,
                type: outputFormat,
                title: "Feature")
        };

        foreach (var format in OgcFeaturesUtilities.FeatureFormats)
        {
            if (string.Equals(format.MediaType, outputFormat, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            links.Add(Link.Create(
                href: $"{basePath}?f={Uri.EscapeDataString(format.QueryValue)}",
                rel: RelationTypes.Alternate,
                type: format.MediaType,
                title: format.Title));
        }

        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/features/collections/{collectionId}",
            rel: RelationTypes.Collection,
            type: MediaTypes.Json,
            title: "Collection"));

        return links.ToImmutableArray();
    }

    private static async Task InvalidateCacheAsync(
        HttpContext context,
        int layerId,
        CancellationToken cancellationToken)
    {
        var cacheInvalidator = context.RequestServices.GetService<OutputCacheInvalidationService>();
        if (cacheInvalidator != null)
        {
            await cacheInvalidator.InvalidateLayerAsync(null, layerId, cancellationToken);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 5212, Level = LogLevel.Error, Message = "OGC create feature failed for collection {CollectionId}")]
        public static partial void CreateFeatureFailed(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 5213, Level = LogLevel.Error, Message = "OGC update feature failed for collection {CollectionId}")]
        public static partial void UpdateFeatureFailed(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 5214, Level = LogLevel.Error, Message = "OGC delete feature failed for collection {CollectionId}")]
        public static partial void DeleteFeatureFailed(ILogger logger, string collectionId, Exception exception);
    }
}
