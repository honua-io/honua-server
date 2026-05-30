// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Stac.Models;
using Honua.Protocols.Stac.Services;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Protocols.Stac;

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

            var visible = await StacV2Lookups.ResolveVisibleStacPublicationsAsync(context, cancellationToken)
                .ConfigureAwait(false);

            var collections = await CollectionsEndpoints.ProjectWithLimitedConcurrencyAsync(
                visible,
                (resolved, ct) => StacMappingService.MapResourceToCollectionAsync(
                    resolved.Resource,
                    resolved.Publication,
                    resolved.LayerIndex,
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

            var resolved = await StacV2Lookups.ResolveStacPublicationAsync(
                context, collectionId, cancellationToken).ConfigureAwait(false);
            if (resolved is null)
            {
                StacTelemetry.SetFailed(activity, "collection_not_found");
                StacLog.CollectionNotFound(logger, collectionId);
                return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
            }

            var accessError = Honua.Infrastructure.Authentication.AccessPolicyHelpers
                .RequireResourceAccess(context, resolved.Value.Resource, resolved.Value.Service);
            if (accessError != null)
            {
                StacTelemetry.SetFailed(activity, "collection_forbidden");
                return accessError;
            }

            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var collection = await StacMappingService.MapResourceToCollectionAsync(
                resolved.Value.Resource,
                resolved.Value.Publication,
                resolved.Value.LayerIndex,
                featureReader,
                baseUrl,
                coordinateTransformService,
                cancellationToken).ConfigureAwait(false);

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
