// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Ogc.Api.Features;
using Honua.Server.Features.Protocols.Stac.Models;
using Honua.Server.Features.Protocols.Stac.Services;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Protocols.Stac;

/// <summary>
/// STAC collection listing and detail endpoints.
/// </summary>
internal static class CollectionEndpoints
{
    /// <summary>
    /// Maps STAC collection endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapStacCollectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/stac/collections", HandleGetCollections)
            .WithDisplayName("STAC Collections")
            .WithName("StacCollections")
            .WithSummary("Get all STAC collections")
            .WithDescription("Lists all available STAC collections")
            .WithTags("STAC")
            .CacheOutput("StacCollections")
            .WithETag()
            .Produces<StacCollectionsResponse>(200, MediaTypes.Json)
            .Produces(404);

        endpoints.MapGet("/stac/collections/{collectionId}", HandleGetCollection)
            .WithDisplayName("STAC Collection")
            .WithName("StacCollection")
            .WithSummary("Get a STAC collection by ID")
            .WithDescription("Returns a single STAC collection with extent and links")
            .WithTags("STAC")
            .CacheOutput("StacCollection")
            .WithETag()
            .Produces<StacCollection>(200, MediaTypes.Json)
            .Produces(404);

        return endpoints;
    }

    private static async Task<IResult> HandleGetCollections(
        HttpContext context,
        [FromServices] ILayerCatalog layerCatalog,
        [FromServices] IFeatureReader featureReader,
        [FromServices] ICoordinateTransformService? coordinateTransformService,
        [FromServices] ILogger<StacEndpoints.StacEndpointsLog> logger)
    {
        using var activity = StacTelemetry.StartActivity(
            StacTelemetry.Operations.Collections,
            "/stac/collections",
            HttpMethods.Get);
        StacLog.CollectionsRequested(logger);

        var validationError = OgcCommonUtilities.ValidateQueryParameters(
            context.Request, StacConstants.AllowedQueryParameters.Collections);
        if (validationError is not null)
        {
            StacTelemetry.SetFailed(activity, "invalid_query_parameters");
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        try
        {
            var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var stacBase = $"{baseUrl}/stac";

            var visibleLayers = await StacFilterHelpers.ResolveStacVisibleLayersAsync(
                context, layerCatalog, cancellationToken);

            var collections = await CollectionsEndpoints.ProjectWithLimitedConcurrencyAsync(
                visibleLayers,
                (layer, ct) => StacMappingService.MapLayerToCollectionAsync(
                    layer,
                    featureReader,
                    baseUrl,
                    coordinateTransformService,
                    ct),
                cancellationToken).ConfigureAwait(false);

            var links = ImmutableArray.Create(
                Link.Create(
                    href: $"{stacBase}/collections",
                    rel: RelationTypes.Self,
                    type: MediaTypes.Json,
                    title: "Collections"),
                Link.Create(
                    href: stacBase,
                    rel: StacConstants.StacRelations.Root,
                    type: MediaTypes.Json,
                    title: "STAC Catalog"));

            var response = new StacCollectionsResponse
            {
                Collections = collections,
                Links = links
            };

            StacLog.CollectionsReturned(logger, collections.Length);
            StacTelemetry.SetResultCount(activity, collections.Length);
            return Results.Json(response, StacJsonContext.Default.StacCollectionsResponse, MediaTypes.Json);
        }
        catch (OperationCanceledException ex)
            when (TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            StacTelemetry.RecordException(activity, ex);
            throw;
        }
        catch (Exception ex)
        {
            StacTelemetry.RecordException(activity, ex);
            StacLog.OperationFailed(logger, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context, "An error occurred while retrieving STAC collections.");
        }
    }

    private static async Task<IResult> HandleGetCollection(
        string collectionId,
        HttpContext context,
        [FromServices] IFeatureReader featureReader,
        [FromServices] ICoordinateTransformService? coordinateTransformService,
        [FromServices] ILogger<StacEndpoints.StacEndpointsLog> logger)
    {
        using var activity = StacTelemetry.StartActivity(
            StacTelemetry.Operations.Collection,
            "/stac/collections/{collectionId}",
            HttpMethods.Get,
            collectionId);
        StacLog.CollectionRequested(logger, collectionId);

        try
        {
            var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
            var validation = await LayerValidationHelpers.ValidateCollectionWithAccessAsync(
                context, collectionId, requiredProtocol: ServiceProtocols.Stac, cancellationToken: cancellationToken);
            if (!validation.IsValid)
            {
                StacTelemetry.SetFailed(activity, "collection_not_found_or_forbidden");
                StacLog.CollectionNotFound(logger, collectionId);
                return validation.ErrorResult!;
            }

            var layer = validation.Layer!;
            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var collection = await StacMappingService.MapLayerToCollectionAsync(
                layer,
                featureReader,
                baseUrl,
                coordinateTransformService,
                cancellationToken);

            StacTelemetry.SetResultCount(activity, 1);
            return Results.Json(collection, StacJsonContext.Default.StacCollection, MediaTypes.Json);
        }
        catch (OperationCanceledException ex)
            when (TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            StacTelemetry.RecordException(activity, ex);
            throw;
        }
        catch (Exception ex)
        {
            StacTelemetry.RecordException(activity, ex);
            StacLog.OperationFailed(logger, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context, "An error occurred while retrieving the STAC collection.");
        }
    }
}
