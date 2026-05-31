// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Read-only Esri-compatible ImageServer surface; anonymous by design. POST
// variants of exportImage/identify/query/computeStatistics mirror the GET form
// and perform no server-side mutation. Each route group opts into
// AllowAnonymous explicitly so authorization-policy tooling can see the intent
// rather than treating it as an accidental gap.

using System.Globalization;
using System.Text.Json;
using Honua.Protocols.GeoServices;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.ImageServer;

/// <summary>
/// Esri Image Server endpoints providing raster imagery capabilities.
/// Supports the Esri Image Server REST API specification.
/// </summary>
internal static class ImageServerEndpoints
{
    private const string JsonContentType = "application/json";
    private const string InlineImageFormat = "image";

    /// <summary>
    /// Maps Image Server endpoints to the application.
    /// </summary>
    public static void MapImageServerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/rest/services/{id:int}/ImageServer")
            .WithTags("ImageServer")
            // Read-only Esri ImageServer surface; access is enforced by the
            // handlers via the layer access policy.
            .AllowAnonymous();

        // Service metadata endpoint
        group.MapGet("", GetServiceInfo)
            .WithDisplayName("Get Image Service Info")
            .WithName("GetImageServiceInfo")
            .WithSummary("Get Image Server service metadata")
            .WithDescription("Returns comprehensive metadata about the image service including extent, capabilities, and raster properties")
            .Produces<ImageServerServiceInfo>()
            .Produces(404)
            .CacheOutput("ImageServerMetadata");
        // Esri clients hydrate metadata by POSTing {"f":"json"}; mirror the GET
        // root so discovery succeeds. The group is already AllowAnonymous and the
        // POST companion omits CacheOutput to match the other POST variants.
        group.MapPost("", GetServiceInfo)
            .WithDisplayName("Get Image Service Info (POST)")
            .WithName("GetImageServiceInfoPost")
            .WithSummary("Get Image Server service metadata using POST")
            .WithDescription("Returns comprehensive metadata about the image service including extent, capabilities, and raster properties")
            .Produces<ImageServerServiceInfo>()
            .Produces(404);

        // Export image endpoint - core rendering capability
        group.MapGet("/exportImage", ExportImage)
            .WithDisplayName("Export Image")
            .WithName("ExportImage")
            .WithSummary("Export rendered raster image")
            .WithDescription("Exports a rendered image from the raster dataset with optional clipping, resampling, and format conversion")
            .Produces<ExportImageResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status200OK, contentType: "image/png")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .Produces(StatusCodes.Status200OK, contentType: "image/tiff")
            .Produces(400)
            .Produces(404)
            .Produces(501);
        group.MapPost("/exportImage", ExportImagePost)
            .WithDisplayName("Export Image (POST)")
            .WithName("ExportImagePost")
            .WithSummary("Export rendered raster image")
            .WithDescription("Exports a rendered image from the raster dataset with optional clipping, resampling, and format conversion")
            .Produces<ExportImageResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status200OK, contentType: "image/png")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .Produces(StatusCodes.Status200OK, contentType: "image/tiff")
            .Produces(400)
            .Produces(404)
            .Produces(501);

        // Identify endpoint - pixel value query
        group.MapGet("/identify", Identify)
            .WithDisplayName("Identify Pixel Values")
            .WithName("Identify")
            .WithSummary("Get pixel values at a point")
            .WithDescription("Returns pixel values and metadata for all bands at the specified geographic location")
            .Produces<IdentifyResponse>()
            .Produces(400)
            .Produces(404);
        group.MapPost("/identify", IdentifyPost)
            .WithDisplayName("Identify Pixel Values (POST)")
            .WithName("IdentifyPost")
            .WithSummary("Get pixel values at a point")
            .WithDescription("Returns pixel values and metadata for all bands at the specified geographic location")
            .Produces<IdentifyResponse>()
            .Produces(400)
            .Produces(404);

        // Image tile endpoint - pre-tiled access
        group.MapGet("/tile/{level}/{row}/{col}", GetImageTile)
            .WithDisplayName("Get Image Tile")
            .WithName("GetImageTile")
            .WithSummary("Get pre-tiled image")
            .WithDescription("Returns a pre-generated image tile for efficient web mapping display")
            .Produces(200, contentType: "image/png")
            .Produces(200, contentType: "image/jpeg")
            .Produces(200, contentType: "image/tiff")
            .Produces(204)
            .Produces(404);

        // Catalog query endpoint - exposes raster catalog as Esri features
        group.MapGet("/query", QueryCatalogGet)
            .WithDisplayName("Query Image Catalog (GET)")
            .WithName("QueryImageCatalogGet")
            .WithSummary("Query the raster catalog with where/objectIds filters")
            .WithDescription("Returns Esri-compatible raster catalog features for the layer with optional WHERE filtering and pagination")
            .Produces<CatalogQueryResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(400)
            .Produces(404);

        group.MapPost("/query", QueryCatalogPost)
            .WithDisplayName("Query Image Catalog (POST)")
            .WithName("QueryImageCatalogPost")
            .WithSummary("Query the raster catalog with where/objectIds filters via POST")
            .WithDescription("POST equivalent of the GET catalog query endpoint that accepts form/JSON bodies")
            .Produces<CatalogQueryResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(400)
            .Produces(404);

        // Compute statistics + histograms for the layer's primary raster
        group.MapGet("/computeStatisticsHistograms", ComputeStatisticsHistogramsGet)
            .WithDisplayName("Compute Statistics Histograms (GET)")
            .WithName("ComputeStatisticsHistogramsGet")
            .WithSummary("Compute per-band statistics and histograms")
            .WithDescription("ArcGIS ImageServer computeStatisticsHistograms contract. Requires geometry and geometryType; returns 501 until geometry-scoped raster statistics are implemented.")
            .Produces(400)
            .Produces(404)
            .Produces(501);

        group.MapPost("/computeStatisticsHistograms", ComputeStatisticsHistogramsPost)
            .WithDisplayName("Compute Statistics Histograms (POST)")
            .WithName("ComputeStatisticsHistogramsPost")
            .WithSummary("Compute per-band statistics and histograms via POST")
            .WithDescription("POST equivalent of the ArcGIS ImageServer computeStatisticsHistograms endpoint")
            .Produces(400)
            .Produces(404)
            .Produces(501);

        // Legend endpoint - per-class swatches for the layer renderer
        group.MapGet("/legend", GetLegend)
            .WithDisplayName("Get Image Server Legend")
            .WithName("GetImageServerLegend")
            .WithSummary("Get raster legend swatches")
            .WithDescription("Returns Esri-compatible legend swatches for the layer's primary raster")
            .Produces<LegendResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(400)
            .Produces(404);

        // Compute class statistics - public ArcGIS route. The internal raster-function
        // analyzer is not exposed here because /computeClass is not an ArcGIS ImageServer
        // contract.
        group.MapGet("/computeClassStatistics", ComputeClassStatisticsGet)
            .WithDisplayName("Compute Class Statistics (GET)")
            .WithName("ImageServerComputeClassStatisticsGet")
            .WithSummary("Compute class statistics signatures")
            .WithDescription("ArcGIS ImageServer computeClassStatistics contract. Requires classDescriptions; returns 501 until class signature computation is implemented.")
            .Produces(400)
            .Produces(404)
            .Produces(501);

        group.MapPost("/computeClassStatistics", ComputeClassStatisticsPost)
            .WithDisplayName("Compute Class Statistics (POST)")
            .WithName("ImageServerComputeClassStatisticsPost")
            .WithSummary("Compute class statistics signatures via POST")
            .WithDescription("POST equivalent of the ArcGIS ImageServer computeClassStatistics endpoint")
            .Produces(400)
            .Produces(404)
            .Produces(501);

        var serviceGroup = app.MapGroup("/rest/services/{serviceId:regex(^(?!\\d+$).+$)}/ImageServer")
            .WithTags("ImageServer")
            // Read-only Esri ImageServer surface; access is enforced by the
            // handlers via the layer access policy.
            .AllowAnonymous();

        serviceGroup.MapGet("", GetServiceInfoByService)
            .WithDisplayName("Get Image Service Info by Service")
            .WithName("GetImageServiceInfoByService")
            .WithSummary("Get Image Server service metadata")
            .WithDescription("Returns comprehensive metadata about the named image service")
            .Produces<ImageServerServiceInfo>()
            .Produces(404)
            .CacheOutput("ImageServerMetadata");
        serviceGroup.MapPost("", GetServiceInfoByService)
            .WithDisplayName("Get Image Service Info by Service (POST)")
            .WithName("GetImageServiceInfoByServicePost")
            .WithSummary("Get Image Server service metadata using POST")
            .WithDescription("Returns comprehensive metadata about the named image service")
            .Produces<ImageServerServiceInfo>()
            .Produces(404);

        serviceGroup.MapGet("/exportImage", ExportImageByService)
            .WithDisplayName("Export Image by Service")
            .WithName("ExportImageByService")
            .WithSummary("Export rendered raster image")
            .Produces<ExportImageResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status200OK, contentType: "image/png")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .Produces(StatusCodes.Status200OK, contentType: "image/tiff")
            .Produces(400)
            .Produces(404)
            .Produces(501);
        serviceGroup.MapPost("/exportImage", ExportImagePostByService)
            .WithDisplayName("Export Image by Service (POST)")
            .WithName("ExportImagePostByService")
            .WithSummary("Export rendered raster image")
            .Produces<ExportImageResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status200OK, contentType: "image/png")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .Produces(StatusCodes.Status200OK, contentType: "image/tiff")
            .Produces(400)
            .Produces(404)
            .Produces(501);

        serviceGroup.MapGet("/identify", IdentifyByService)
            .WithDisplayName("Identify Pixel Values by Service")
            .WithName("IdentifyByService")
            .WithSummary("Get pixel values at a point")
            .Produces<IdentifyResponse>()
            .Produces(400)
            .Produces(404);
        serviceGroup.MapPost("/identify", IdentifyPostByService)
            .WithDisplayName("Identify Pixel Values by Service (POST)")
            .WithName("IdentifyPostByService")
            .WithSummary("Get pixel values at a point")
            .Produces<IdentifyResponse>()
            .Produces(400)
            .Produces(404);

        serviceGroup.MapGet("/tile/{level}/{row}/{col}", GetImageTileByService)
            .WithDisplayName("Get Image Tile by Service")
            .WithName("GetImageTileByService")
            .WithSummary("Get pre-tiled image")
            .Produces(200, contentType: "image/png")
            .Produces(200, contentType: "image/jpeg")
            .Produces(200, contentType: "image/tiff")
            .Produces(204)
            .Produces(404);

        serviceGroup.MapGet("/query", QueryCatalogGetByService)
            .WithDisplayName("Query Image Catalog by Service (GET)")
            .WithName("QueryImageCatalogGetByService")
            .WithSummary("Query the raster catalog")
            .Produces<CatalogQueryResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(400)
            .Produces(404);
        serviceGroup.MapPost("/query", QueryCatalogPostByService)
            .WithDisplayName("Query Image Catalog by Service (POST)")
            .WithName("QueryImageCatalogPostByService")
            .WithSummary("Query the raster catalog")
            .Produces<CatalogQueryResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(400)
            .Produces(404);

        serviceGroup.MapGet("/computeStatisticsHistograms", ComputeStatisticsHistogramsGetByService)
            .WithDisplayName("Compute Statistics Histograms by Service (GET)")
            .WithName("ComputeStatisticsHistogramsGetByService")
            .WithSummary("Compute per-band statistics and histograms")
            .Produces(400)
            .Produces(404)
            .Produces(501);
        serviceGroup.MapPost("/computeStatisticsHistograms", ComputeStatisticsHistogramsPostByService)
            .WithDisplayName("Compute Statistics Histograms by Service (POST)")
            .WithName("ComputeStatisticsHistogramsPostByService")
            .WithSummary("Compute per-band statistics and histograms")
            .Produces(400)
            .Produces(404)
            .Produces(501);

        serviceGroup.MapGet("/legend", GetLegendByService)
            .WithDisplayName("Get Image Server Legend by Service")
            .WithName("GetImageServerLegendByService")
            .WithSummary("Get raster legend swatches")
            .Produces<LegendResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(400)
            .Produces(404);

        serviceGroup.MapGet("/computeClassStatistics", ComputeClassStatisticsGetByService)
            .WithDisplayName("Compute Class Statistics by Service (GET)")
            .WithName("ImageServerComputeClassStatisticsGetByService")
            .WithSummary("Compute class statistics signatures")
            .Produces(400)
            .Produces(404)
            .Produces(501);
        serviceGroup.MapPost("/computeClassStatistics", ComputeClassStatisticsPostByService)
            .WithDisplayName("Compute Class Statistics by Service (POST)")
            .WithName("ImageServerComputeClassStatisticsPostByService")
            .WithSummary("Compute class statistics signatures")
            .Produces(400)
            .Produces(404)
            .Produces(501);
    }

    /// <summary>
    /// Get Image Server service metadata.
    /// </summary>
    private static async Task<IResult> GetServiceInfo(
        int id,
        string? f,
        HttpContext context,
        ImageServerMetadataHandler handler,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupportedJsonResponseFormat(f))
        {
            return CreateUnsupportedJsonFormatResult(context);
        }

        var layerError = await ValidateImageLayerAsync(id, context, cancellationToken);
        if (layerError is not null)
        {
            return layerError;
        }

        return await handler.GetServiceInfoAsync(context, id, cancellationToken);
    }

    private static async Task<IResult> GetServiceInfoByService(
        string serviceId,
        string? f,
        HttpContext context,
        ImageServerMetadataHandler handler,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveImageServiceLayerIdAsync(serviceId, context, cancellationToken);
        return resolution.ErrorResult ?? await GetServiceInfo(resolution.LayerId, f, context, handler, cancellationToken);
    }

    /// <summary>
    /// Export rendered image from raster data.
    /// </summary>
    private static async Task<IResult> ExportImage(
        int id,
        [AsParameters] ExportImageRequest request,
        HttpContext context,
        ImageServerExportHandler handler,
        CancellationToken cancellationToken = default)
        => await ExecuteExportImageAsync(id, request, context, handler, cancellationToken);

    private static async Task<IResult> ExportImageByService(
        string serviceId,
        [AsParameters] ExportImageRequest request,
        HttpContext context,
        ImageServerExportHandler handler,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveImageServiceLayerIdAsync(serviceId, context, cancellationToken);
        return resolution.ErrorResult ?? await ExecuteExportImageAsync(resolution.LayerId, request, context, handler, cancellationToken);
    }

    private static async Task<IResult> ExportImagePost(
        int id,
        HttpContext context,
        ImageServerExportHandler handler,
        CancellationToken cancellationToken = default)
    {
        var bodyValues = await ReadPostValuesAsync(context, cancellationToken);
        if (bodyValues.Error != null)
        {
            return bodyValues.Error;
        }

        var request = CreateExportImageRequest(MergeQueryAndBodyValues(context, bodyValues.Values!));
        return await ExecuteExportImageAsync(id, request, context, handler, cancellationToken);
    }

    private static async Task<IResult> ExportImagePostByService(
        string serviceId,
        HttpContext context,
        ImageServerExportHandler handler,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveImageServiceLayerIdAsync(serviceId, context, cancellationToken);
        if (resolution.ErrorResult is not null)
        {
            return resolution.ErrorResult;
        }

        var bodyValues = await ReadPostValuesAsync(context, cancellationToken);
        if (bodyValues.Error != null)
        {
            return bodyValues.Error;
        }

        var request = CreateExportImageRequest(MergeQueryAndBodyValues(context, bodyValues.Values!));
        return await ExecuteExportImageAsync(resolution.LayerId, request, context, handler, cancellationToken);
    }

    private static async Task<IResult> ExecuteExportImageAsync(
        int id,
        ExportImageRequest request,
        HttpContext context,
        ImageServerExportHandler handler,
        CancellationToken cancellationToken)
    {
        if (!IsSupportedExportResponseFormat(request.F))
        {
            return CreateUnsupportedExportFormatResult(context);
        }

        var layerError = await ValidateImageLayerAsync(id, context, cancellationToken);
        if (layerError is not null)
        {
            return layerError;
        }

        return await handler.ExportImageAsync(context, id, request, cancellationToken);
    }

    /// <summary>
    /// Identify pixel values at a geographic point.
    /// </summary>
    private static async Task<IResult> Identify(
        int id,
        [AsParameters] IdentifyRequest request,
        HttpContext context,
        ImageServerIdentifyHandler handler,
        CancellationToken cancellationToken = default)
        => await ExecuteIdentifyAsync(id, request, context, handler, cancellationToken);

    private static async Task<IResult> IdentifyByService(
        string serviceId,
        [AsParameters] IdentifyRequest request,
        HttpContext context,
        ImageServerIdentifyHandler handler,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveImageServiceLayerIdAsync(serviceId, context, cancellationToken);
        return resolution.ErrorResult ?? await ExecuteIdentifyAsync(resolution.LayerId, request, context, handler, cancellationToken);
    }

    private static async Task<IResult> IdentifyPost(
        int id,
        HttpContext context,
        ImageServerIdentifyHandler handler,
        CancellationToken cancellationToken = default)
    {
        var bodyValues = await ReadPostValuesAsync(context, cancellationToken);
        if (bodyValues.Error != null)
        {
            return bodyValues.Error;
        }

        var request = CreateIdentifyRequest(MergeQueryAndBodyValues(context, bodyValues.Values!));
        return await ExecuteIdentifyAsync(id, request, context, handler, cancellationToken);
    }

    private static async Task<IResult> IdentifyPostByService(
        string serviceId,
        HttpContext context,
        ImageServerIdentifyHandler handler,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveImageServiceLayerIdAsync(serviceId, context, cancellationToken);
        if (resolution.ErrorResult is not null)
        {
            return resolution.ErrorResult;
        }

        var bodyValues = await ReadPostValuesAsync(context, cancellationToken);
        if (bodyValues.Error != null)
        {
            return bodyValues.Error;
        }

        var request = CreateIdentifyRequest(MergeQueryAndBodyValues(context, bodyValues.Values!));
        return await ExecuteIdentifyAsync(resolution.LayerId, request, context, handler, cancellationToken);
    }

    private static async Task<IResult> ExecuteIdentifyAsync(
        int id,
        IdentifyRequest request,
        HttpContext context,
        ImageServerIdentifyHandler handler,
        CancellationToken cancellationToken)
    {
        if (!IsSupportedJsonResponseFormat(request.F))
        {
            return CreateUnsupportedJsonFormatResult(context);
        }

        var layerError = await ValidateImageLayerAsync(id, context, cancellationToken);
        if (layerError is not null)
        {
            return layerError;
        }

        return await handler.IdentifyAsync(context, id, request, cancellationToken);
    }

    /// <summary>
    /// Query the raster catalog using GET parameters.
    /// </summary>
    private static async Task<IResult> QueryCatalogGet(
        int id,
        HttpContext context,
        ImageServerCatalogQueryHandler handler,
        CancellationToken cancellationToken = default)
    {
        var layerError = await ValidateImageLayerAsync(id, context, cancellationToken);
        if (layerError is not null)
        {
            return layerError;
        }

        var values = GeoServicesRequestValueHelpers.ToCaseInsensitiveDictionary(context.Request.Query);
        return await handler.QueryCatalogAsync(context, id, values, cancellationToken);
    }

    private static async Task<IResult> QueryCatalogGetByService(
        string serviceId,
        HttpContext context,
        ImageServerCatalogQueryHandler handler,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveImageServiceLayerIdAsync(serviceId, context, cancellationToken);
        return resolution.ErrorResult ?? await QueryCatalogGet(resolution.LayerId, context, handler, cancellationToken);
    }

    /// <summary>
    /// Query the raster catalog using POST body (JSON or form-encoded).
    /// </summary>
    private static async Task<IResult> QueryCatalogPost(
        int id,
        HttpContext context,
        ImageServerCatalogQueryHandler handler,
        CancellationToken cancellationToken = default)
    {
        var layerError = await ValidateImageLayerAsync(id, context, cancellationToken);
        if (layerError is not null)
        {
            return layerError;
        }

        var (bodyValues, readError) = await GeoServicesRequestValueHelpers.TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (bodyValues is null)
        {
            if (GeoServicesRequestValueHelpers.TryGetUnsupportedMediaType(readError, out var receivedContentType))
            {
                return GeoServicesRequestValueHelpers.CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
            }

            return StandardErrorHelpers.CreateBadRequest(context, readError ?? "Invalid request body.");
        }

        var merged = GeoServicesRequestValueHelpers.ToCaseInsensitiveDictionary(context.Request.Query);
        foreach (var pair in bodyValues)
        {
            merged[pair.Key] = pair.Value;
        }

        return await handler.QueryCatalogAsync(context, id, merged, cancellationToken);
    }

    private static async Task<IResult> QueryCatalogPostByService(
        string serviceId,
        HttpContext context,
        ImageServerCatalogQueryHandler handler,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveImageServiceLayerIdAsync(serviceId, context, cancellationToken);
        return resolution.ErrorResult ?? await QueryCatalogPost(resolution.LayerId, context, handler, cancellationToken);
    }

    /// <summary>
    /// Compute statistics and histograms for the primary raster (GET).
    /// </summary>
    private static async Task<IResult> ComputeStatisticsHistogramsGet(
        int id,
        HttpContext context,
        ImageServerStatisticsHistogramsHandler handler,
        CancellationToken cancellationToken = default)
    {
        var layerError = await ValidateImageLayerAsync(id, context, cancellationToken);
        if (layerError is not null)
        {
            return layerError;
        }

        var values = GeoServicesRequestValueHelpers.ToCaseInsensitiveDictionary(context.Request.Query);
        if (!IsSupportedJsonResponseFormat(GetString(values, "f")))
        {
            return CreateUnsupportedJsonFormatResult(context);
        }

        if (!TryValidateComputeStatisticsHistogramsRequest(values, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context, error ?? "Invalid request.");
        }

        return StandardErrorHelpers.CreateNotImplemented(
            context,
            "computeStatisticsHistograms is not yet implemented on this service.");
    }

    private static async Task<IResult> ComputeStatisticsHistogramsGetByService(
        string serviceId,
        HttpContext context,
        ImageServerStatisticsHistogramsHandler handler,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveImageServiceLayerIdAsync(serviceId, context, cancellationToken);
        return resolution.ErrorResult ?? await ComputeStatisticsHistogramsGet(resolution.LayerId, context, handler, cancellationToken);
    }

    /// <summary>
    /// Compute statistics and histograms for the primary raster (POST).
    /// </summary>
    private static async Task<IResult> ComputeStatisticsHistogramsPost(
        int id,
        HttpContext context,
        ImageServerStatisticsHistogramsHandler handler,
        CancellationToken cancellationToken = default)
    {
        var layerError = await ValidateImageLayerAsync(id, context, cancellationToken);
        if (layerError is not null)
        {
            return layerError;
        }

        var (bodyValues, readError) = await GeoServicesRequestValueHelpers.TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (bodyValues is null)
        {
            if (GeoServicesRequestValueHelpers.TryGetUnsupportedMediaType(readError, out var receivedContentType))
            {
                return GeoServicesRequestValueHelpers.CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
            }

            return StandardErrorHelpers.CreateBadRequest(context, readError ?? "Invalid request body.");
        }

        var merged = GeoServicesRequestValueHelpers.ToCaseInsensitiveDictionary(context.Request.Query);
        foreach (var pair in bodyValues)
        {
            merged[pair.Key] = pair.Value;
        }

        if (!IsSupportedJsonResponseFormat(GetString(merged, "f")))
        {
            return CreateUnsupportedJsonFormatResult(context);
        }

        if (!TryValidateComputeStatisticsHistogramsRequest(merged, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context, error ?? "Invalid request.");
        }

        return StandardErrorHelpers.CreateNotImplemented(
            context,
            "computeStatisticsHistograms is not yet implemented on this service.");
    }

    private static async Task<IResult> ComputeStatisticsHistogramsPostByService(
        string serviceId,
        HttpContext context,
        ImageServerStatisticsHistogramsHandler handler,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveImageServiceLayerIdAsync(serviceId, context, cancellationToken);
        return resolution.ErrorResult ?? await ComputeStatisticsHistogramsPost(resolution.LayerId, context, handler, cancellationToken);
    }

    /// <summary>
    /// Get the per-class legend swatches for the layer's primary raster.
    /// </summary>
    private static async Task<IResult> GetLegend(
        int id,
        string? f,
        HttpContext context,
        ImageServerLegendHandler handler,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupportedJsonResponseFormat(f))
        {
            return CreateUnsupportedJsonFormatResult(context);
        }

        var layerError = await ValidateImageLayerAsync(id, context, cancellationToken);
        if (layerError is not null)
        {
            return layerError;
        }

        return await handler.GetLegendAsync(context, id, cancellationToken);
    }

    private static async Task<IResult> GetLegendByService(
        string serviceId,
        string? f,
        HttpContext context,
        ImageServerLegendHandler handler,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveImageServiceLayerIdAsync(serviceId, context, cancellationToken);
        return resolution.ErrorResult ?? await GetLegend(resolution.LayerId, f, context, handler, cancellationToken);
    }

    /// <summary>
    /// Validate public computeClassStatistics GET parameters and return the current implementation status.
    /// </summary>
    private static async Task<IResult> ComputeClassStatisticsGet(
        int id,
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        var layerError = await ValidateImageLayerAsync(id, context, cancellationToken);
        if (layerError is not null)
        {
            return layerError;
        }

        var values = GeoServicesRequestValueHelpers.ToCaseInsensitiveDictionary(context.Request.Query);
        if (!IsSupportedJsonResponseFormat(GetString(values, "f")))
        {
            return CreateUnsupportedJsonFormatResult(context);
        }

        if (!TryValidateComputeClassStatisticsRequest(values, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context, error ?? "Invalid request.");
        }

        return StandardErrorHelpers.CreateNotImplemented(
            context,
            "computeClassStatistics is not yet implemented on this service.");
    }

    private static async Task<IResult> ComputeClassStatisticsGetByService(
        string serviceId,
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveImageServiceLayerIdAsync(serviceId, context, cancellationToken);
        return resolution.ErrorResult ?? await ComputeClassStatisticsGet(resolution.LayerId, context, cancellationToken);
    }

    /// <summary>
    /// Validate public computeClassStatistics POST parameters and return the current implementation status.
    /// </summary>
    private static async Task<IResult> ComputeClassStatisticsPost(
        int id,
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        var layerError = await ValidateImageLayerAsync(id, context, cancellationToken);
        if (layerError is not null)
        {
            return layerError;
        }

        var (bodyValues, readError) = await GeoServicesRequestValueHelpers.TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (bodyValues is null)
        {
            if (GeoServicesRequestValueHelpers.TryGetUnsupportedMediaType(readError, out var receivedContentType))
            {
                return GeoServicesRequestValueHelpers.CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
            }

            return StandardErrorHelpers.CreateBadRequest(context, readError ?? "Invalid request body.");
        }

        var merged = GeoServicesRequestValueHelpers.ToCaseInsensitiveDictionary(context.Request.Query);
        foreach (var pair in bodyValues)
        {
            merged[pair.Key] = pair.Value;
        }

        if (!IsSupportedJsonResponseFormat(GetString(merged, "f")))
        {
            return CreateUnsupportedJsonFormatResult(context);
        }

        if (!TryValidateComputeClassStatisticsRequest(merged, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context, error ?? "Invalid request.");
        }

        return StandardErrorHelpers.CreateNotImplemented(
            context,
            "computeClassStatistics is not yet implemented on this service.");
    }

    private static async Task<IResult> ComputeClassStatisticsPostByService(
        string serviceId,
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveImageServiceLayerIdAsync(serviceId, context, cancellationToken);
        return resolution.ErrorResult ?? await ComputeClassStatisticsPost(resolution.LayerId, context, cancellationToken);
    }

    /// <summary>
    /// Get pre-tiled image for efficient web mapping.
    /// </summary>
    private static async Task<IResult> GetImageTile(
        int id,
        int level,
        int row,
        int col,
        HttpContext context,
        ImageServerTileHandler handler,
        string format = "png",
        CancellationToken cancellationToken = default)
    {
        var layerError = await ValidateImageLayerAsync(id, context, cancellationToken);
        if (layerError is not null)
        {
            return layerError;
        }

        return await handler.GetImageTileAsync(context, id, level, row, col, format, cancellationToken);
    }

    private static async Task<IResult> GetImageTileByService(
        string serviceId,
        int level,
        int row,
        int col,
        HttpContext context,
        ImageServerTileHandler handler,
        string format = "png",
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveImageServiceLayerIdAsync(serviceId, context, cancellationToken);
        return resolution.ErrorResult ?? await GetImageTile(resolution.LayerId, level, row, col, context, handler, format, cancellationToken);
    }

    private static async Task<(int LayerId, IResult? ErrorResult)> ResolveImageServiceLayerIdAsync(
        string serviceId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var resolver = context.RequestServices.GetRequiredService<IImageServerLayerResolver>();
        var resolution = await resolver.ResolveFirstAccessibleLayerAsync(
            serviceId,
            context,
            cancellationToken).ConfigureAwait(false);
        return (resolution.LayerId, resolution.ErrorResult);
    }

    private static async Task<IResult?> ValidateImageLayerAsync(
        int layerId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var resolver = context.RequestServices.GetRequiredService<IImageServerLayerResolver>();
        var resolution = await resolver.ValidateLayerAsync(
            layerId,
            context,
            cancellationToken).ConfigureAwait(false);
        return resolution.ErrorResult;
    }

    private static bool IsSupportedJsonResponseFormat(string? format)
        => string.IsNullOrWhiteSpace(format) ||
           string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(format, "pjson", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedExportResponseFormat(string? format)
        => IsSupportedJsonResponseFormat(format) ||
           string.Equals(format, InlineImageFormat, StringComparison.OrdinalIgnoreCase);

    private static async Task<(IReadOnlyDictionary<string, StringValues>? Values, IResult? Error)> ReadPostValuesAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var (bodyValues, readError) = await GeoServicesRequestValueHelpers.TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (bodyValues is not null)
        {
            return (bodyValues, null);
        }

        if (GeoServicesRequestValueHelpers.TryGetUnsupportedMediaType(readError, out var receivedContentType))
        {
            return (null, GeoServicesRequestValueHelpers.CreateUnsupportedRequestContentTypeResult(context, receivedContentType));
        }

        return (null, StandardErrorHelpers.CreateBadRequest(context, readError ?? "Invalid request body."));
    }

    private static Dictionary<string, StringValues> MergeQueryAndBodyValues(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> bodyValues)
    {
        var merged = GeoServicesRequestValueHelpers.ToCaseInsensitiveDictionary(context.Request.Query);
        foreach (var pair in bodyValues)
        {
            merged[pair.Key] = pair.Value;
        }

        return merged;
    }

    private static ExportImageRequest CreateExportImageRequest(IReadOnlyDictionary<string, StringValues> values)
        => new()
        {
            Bbox = GetString(values, "bbox"),
            Size = GetString(values, "size"),
            ImageSr = GetString(values, "imageSR") ?? GetString(values, "imageSr"),
            BboxSr = GetString(values, "bboxSR") ?? GetString(values, "bboxSr"),
            Format = GetString(values, "format") ?? "png",
            PixelType = GetString(values, "pixelType"),
            NoData = GetString(values, "noData"),
            NoDataInterpretation = GetString(values, "noDataInterpretation") ?? "esriNoDataMatchAny",
            Interpolation = GetString(values, "interpolation") ?? "RSP_BilinearInterpolation",
            Compression = GetString(values, "compression"),
            CompressionQuality = TryGetInt(values, "compressionQuality"),
            BandIds = GetString(values, "bandIds"),
            MosaicRule = GetString(values, "mosaicRule"),
            RenderingRule = GetString(values, "renderingRule"),
            F = GetString(values, "f") ?? "json"
        };

    private static IdentifyRequest CreateIdentifyRequest(IReadOnlyDictionary<string, StringValues> values)
        => new()
        {
            Geometry = GetString(values, "geometry") ?? string.Empty,
            GeometryType = GetString(values, "geometryType") ?? "esriGeometryPoint",
            Sr = GetString(values, "sr"),
            MosaicRule = GetString(values, "mosaicRule"),
            RenderingRule = GetString(values, "renderingRule"),
            PixelSize = TryGetInt(values, "pixelSize"),
            Time = GetString(values, "time"),
            ReturnGeometry = TryGetBool(values, "returnGeometry") ?? true,
            ReturnCatalogItems = TryGetBool(values, "returnCatalogItems") ?? false,
            F = GetString(values, "f") ?? "json"
        };

    private static int? TryGetInt(IReadOnlyDictionary<string, StringValues> values, string key)
        => int.TryParse(GetString(values, key), CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool? TryGetBool(IReadOnlyDictionary<string, StringValues> values, string key)
        => bool.TryParse(GetString(values, key), out var value) ? value : null;

    private static bool TryValidateComputeStatisticsHistogramsRequest(
        IReadOnlyDictionary<string, StringValues> values,
        out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(GetString(values, "geometry")))
        {
            error = "geometry is required.";
            return false;
        }

        var geometryType = GetString(values, "geometryType");
        if (string.IsNullOrWhiteSpace(geometryType))
        {
            error = "geometryType is required.";
            return false;
        }

        if (!TryValidateEsriGeometry(GetString(values, "geometry"), geometryType, out error))
        {
            return false;
        }

        return true;
    }

    private static bool TryValidateComputeClassStatisticsRequest(
        IReadOnlyDictionary<string, StringValues> values,
        out string? error)
    {
        error = null;

        var classDescriptions = GetString(values, "classDescriptions");
        if (string.IsNullOrWhiteSpace(classDescriptions))
        {
            error = "classDescriptions is required.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(classDescriptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("classes", out var classesElement) ||
                classesElement.ValueKind != JsonValueKind.Array)
            {
                error = "classDescriptions must be a JSON object containing a classes array.";
                return false;
            }
        }
        catch (JsonException)
        {
            error = "classDescriptions must be valid JSON.";
            return false;
        }

        return true;
    }

    private static bool TryValidateEsriGeometry(
        string? geometry,
        string geometryType,
        out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(geometry))
        {
            error = "geometry is required.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(geometry);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "geometry must be a JSON object.";
                return false;
            }

            if (string.Equals(geometryType, "esriGeometryEnvelope", StringComparison.OrdinalIgnoreCase))
            {
                if (!HasNumericProperty(document.RootElement, "xmin") ||
                    !HasNumericProperty(document.RootElement, "ymin") ||
                    !HasNumericProperty(document.RootElement, "xmax") ||
                    !HasNumericProperty(document.RootElement, "ymax"))
                {
                    error = "geometry must be an envelope JSON object with xmin, ymin, xmax, and ymax.";
                    return false;
                }

                return true;
            }

            if (string.Equals(geometryType, "esriGeometryPolygon", StringComparison.OrdinalIgnoreCase))
            {
                if (!document.RootElement.TryGetProperty("rings", out var ringsElement) ||
                    ringsElement.ValueKind != JsonValueKind.Array)
                {
                    error = "geometry must be a polygon JSON object with rings.";
                    return false;
                }

                return true;
            }

            error = "geometryType must be esriGeometryEnvelope or esriGeometryPolygon.";
            return false;
        }
        catch (JsonException)
        {
            error = "geometry must be valid JSON.";
            return false;
        }
    }

    private static bool HasNumericProperty(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number;

    private static string? GetString(IReadOnlyDictionary<string, StringValues> values, string key)
        => values.TryGetValue(key, out var raw) ? raw.ToString() : null;

    private static IResult CreateUnsupportedJsonFormatResult(HttpContext context)
        => StandardErrorHelpers.CreateBadRequest(context, "Only JSON format is supported. Use f=json or f=pjson");

    private static IResult CreateUnsupportedExportFormatResult(HttpContext context)
        => StandardErrorHelpers.CreateBadRequest(context, "Only JSON and image formats are supported. Use f=json, f=pjson, or f=image");
}
