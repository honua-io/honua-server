// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcFeatures.Models;
using Honua.Server.Features.OgcFeatures.Services;
using Honua.ServiceDefaults;

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
            var layerValidation = await LayerValidationHelpers.ValidateCollectionWriteAccessAsync(
                context, collectionId, cancellationToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }
            var layer = layerValidation.Layer!;
            var layerId = layer.Id;

            using var activity = HonuaTelemetry.ActivitySource.StartActivity(
                HonuaTelemetry.Activities.FeatureEdit, ActivityKind.Internal);
            activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OgcFeatures);
            activity?.SetTag(HonuaTelemetry.Tags.Operation, "create");
            activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);

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
            await OgcFeaturesUtilities.InvalidateLayerCacheAsync(context, layerId, cancellationToken);
            var createLinks = OgcFeaturesUtilities.BuildFeatureLinks(
                context.Request,
                collectionId,
                FormattableString.Invariant($"{created.Id}"),
                MediaTypes.GeoJson);
            context.Response.Headers["Content-Crs"] = $"<{inputCrs.Uri}>";
            var featureIdString = FormattableString.Invariant($"{created.Id}");
            var locationUrl = OgcFeaturesUtilities.BuildFeatureSelfUrl(context.Request, collectionId, featureIdString);
            context.Response.Headers.Location = locationUrl;
            var response = ToOgcFeature(created, inputCrs.AxisOrder, createLinks);

            HonuaTelemetry.SetSuccess(activity);
            return Results.Json(response, OgcJsonContext.Default.GeoJsonFeature, contentType: MediaTypes.GeoJson, statusCode: StatusCodes.Status201Created);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ResourceConflictException ex)
        {
            return StandardErrorHelpers.CreateFromException(context, ex);
        }
        catch (Exception ex)
        {
            Log.CreateFeatureFailed(_logger, collectionId, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while creating the feature.");
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
            var layerValidation = await LayerValidationHelpers.ValidateCollectionWriteAccessAsync(
                context, collectionId, cancellationToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }
            var layer = layerValidation.Layer!;
            var layerId = layer.Id;

            using var activity = HonuaTelemetry.ActivitySource.StartActivity(
                HonuaTelemetry.Activities.FeatureEdit, ActivityKind.Internal);
            activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OgcFeatures);
            activity?.SetTag(HonuaTelemetry.Tags.Operation, "delete");
            activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);

            if (!long.TryParse(featureId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectId))
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            var deleted = await _featureWriter.DeleteAsync(layerId, objectId, cancellationToken);
            if (!deleted)
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            await OgcFeaturesUtilities.InvalidateLayerCacheAsync(context, layerId, cancellationToken);
            HonuaTelemetry.SetSuccess(activity);
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

    private static partial class Log
    {
        [LoggerMessage(EventId = 5230, Level = LogLevel.Error, Message = "OGC create feature failed for collection {CollectionId}")]
        public static partial void CreateFeatureFailed(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 5231, Level = LogLevel.Error, Message = "OGC delete feature failed for collection {CollectionId}")]
        public static partial void DeleteFeatureFailed(ILogger logger, string collectionId, Exception exception);
    }
}
