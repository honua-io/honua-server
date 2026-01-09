// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Security;
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
    private readonly IResourceValidator _resourceValidator = dependencies.ResourceValidator;
    private readonly LimitsOptions _limitsOptions = dependencies.LimitsOptions;
    private readonly OgcFeaturesGeometryServices _geometryServices = dependencies.GeometryServices;
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
                context, collectionId, cancellationToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }
            var layer = layerValidation.Layer!;
            var layerId = layer.Id;

            var (requestFeature, requestError) = await ReadGeoJsonFeatureAsync(context, cancellationToken);
            if (requestFeature == null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, requestError ?? "Invalid GeoJSON payload.");
            }

            byte[]? geometryWkb = null;
            if (requestFeature.Geometry != null)
            {
                var wkbResult = _geometryServices.TryCreateWkbFromGeoJson(
                    requestFeature.Geometry,
                    layer.SpatialReference.ToSrid());
                if (!wkbResult.IsSuccess)
                {
                    return StandardErrorHelpers.CreateBadRequest(context, wkbResult.ErrorMessage!);
                }
                geometryWkb = wkbResult.Wkb;
            }

            if (geometryWkb != null)
            {
                // Use centralized geometry validation limits
                var validationResult = WkbValidation.Validate(geometryWkb, _limitsOptions.Validation);
                if (!validationResult.IsValid)
                {
                    return StandardErrorHelpers.CreateBadRequest(context, $"Invalid geometry: {validationResult.ErrorMessage}");
                }
            }

            var attributesResult = layer.ValidateAttributes(
                requestFeature.Properties,
                ValidationExtensions.AttributeValidationMode.Strict);
            if (!attributesResult.IsValid)
            {
                return StandardErrorHelpers.CreateBadRequest(context, attributesResult.ErrorMessage ?? "Invalid attributes.");
            }

            var feature = Feature.Create(0, geometryWkb, attributesResult.Value!);

            var created = await _featureWriter.CreateAsync(layerId, feature, cancellationToken);
            await InvalidateCacheAsync(context, layerId, cancellationToken);
            var createLinks = BuildFeatureLinks(
                context.Request,
                collectionId,
                FormattableString.Invariant($"{created.Id}"),
                MediaTypes.GeoJson);
            var response = ToOgcFeature(created, OgcFeaturesUtilities.AxisOrder.EastNorth, createLinks);

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
            return ProtocolErrorWriter.CreateErrorResult(context, 500, "Internal server error", "An error occurred while creating the feature.");
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
                context, collectionId, cancellationToken);
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

            var (requestFeature, requestError) = await ReadGeoJsonFeatureAsync(context, cancellationToken);
            if (requestFeature == null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, requestError ?? "Invalid GeoJSON payload.");
            }

            byte[]? geometryWkb = null;
            if (requestFeature.Geometry != null)
            {
                var wkbResult = _geometryServices.TryCreateWkbFromGeoJson(
                    requestFeature.Geometry,
                    layer.SpatialReference.ToSrid());
                if (!wkbResult.IsSuccess)
                {
                    return StandardErrorHelpers.CreateBadRequest(context, wkbResult.ErrorMessage!);
                }
                geometryWkb = wkbResult.Wkb;
            }

            if (geometryWkb != null)
            {
                // Use centralized geometry validation limits
                var validationResult = WkbValidation.Validate(geometryWkb, _limitsOptions.Validation);
                if (!validationResult.IsValid)
                {
                    return StandardErrorHelpers.CreateBadRequest(context, $"Invalid geometry: {validationResult.ErrorMessage}");
                }
            }

            var attributesResult = layer.ValidateAttributes(
                requestFeature.Properties,
                ValidationExtensions.AttributeValidationMode.Strict);
            if (!attributesResult.IsValid)
            {
                return StandardErrorHelpers.CreateBadRequest(context, attributesResult.ErrorMessage ?? "Invalid attributes.");
            }

            var feature = Feature.Create(objectId, geometryWkb, attributesResult.Value!);

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
            var response = ToOgcFeature(updated, OgcFeaturesUtilities.AxisOrder.EastNorth, updateLinks);
            return Results.Json(response, OgcJsonContext.Default.GeoJsonFeature, contentType: MediaTypes.GeoJson);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.UpdateFeatureFailed(_logger, collectionId, ex);
            return ProtocolErrorWriter.CreateErrorResult(context, 500, "Internal server error", "An error occurred while updating the feature.");
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
                context, collectionId, cancellationToken);
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
            return ProtocolErrorWriter.CreateErrorResult(context, 500, "Internal server error", "An error occurred while deleting the feature.");
        }
    }

    private static async Task<(GeoJsonFeature? Feature, string? Error)> ReadGeoJsonFeatureAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            var body = await reader.ReadToEndAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return (null, "Request body is required.");
            }

            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, "GeoJSON payload must be an object.");
            }

            if (!document.RootElement.TryGetProperty("type", out var typeProperty))
            {
                return (null, "GeoJSON payload must include a 'type' member.");
            }

            if (!string.Equals(typeProperty.GetString(), "Feature", StringComparison.OrdinalIgnoreCase))
            {
                return (null, "GeoJSON 'type' must be 'Feature'.");
            }

            try
            {
                var feature = JsonSerializer.Deserialize(body, OgcJsonContext.Default.GeoJsonFeature);
                return feature == null
                    ? (null, "Invalid GeoJSON payload.")
                    : (feature, null);
            }
            catch (JsonException)
            {
                return (null, "Invalid GeoJSON payload.");
            }
        }
        catch (JsonException)
        {
            return (null, "Invalid JSON payload.");
        }
    }

    private static GeoJsonFeature ToOgcFeature(
        Feature feature,
        OgcFeaturesUtilities.AxisOrder axisOrder,
        ImmutableArray<Link>? links = null)
    {
        var geometry = OgcFeaturesGeometryServices.ConvertWkbToSimpleGeometry(feature.Geometry, axisOrder);
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
