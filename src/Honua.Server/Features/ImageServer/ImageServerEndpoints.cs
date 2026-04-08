// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.FeatureServer;
using Honua.Server.Features.ImageServer.Handlers;
using Honua.Server.Features.ImageServer.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;

namespace Honua.Server.Features.ImageServer;

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
        var group = app.MapGroup("/rest/services/{id}/ImageServer")
            .WithTags("ImageServer");

        // Service metadata endpoint
        group.MapGet("", GetServiceInfo)
            .WithDisplayName("Get Image Service Info")
            .WithName("GetImageServiceInfo")
            .WithSummary("Get Image Server service metadata")
            .WithDescription("Returns comprehensive metadata about the image service including extent, capabilities, and raster properties")
            .Produces<ImageServerServiceInfo>()
            .Produces(404)
            .CacheOutput("ImageServerMetadata");

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
            .Produces(404);

        // Identify endpoint - pixel value query
        group.MapGet("/identify", Identify)
            .WithDisplayName("Identify Pixel Values")
            .WithName("Identify")
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
            .WithDescription("Returns Esri-compatible statistics and histograms for the layer's primary raster")
            .Produces<ComputeStatisticsHistogramsResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(400)
            .Produces(404);

        group.MapPost("/computeStatisticsHistograms", ComputeStatisticsHistogramsPost)
            .WithDisplayName("Compute Statistics Histograms (POST)")
            .WithName("ComputeStatisticsHistogramsPost")
            .WithSummary("Compute per-band statistics and histograms via POST")
            .WithDescription("POST equivalent of the GET statistics/histograms endpoint")
            .Produces<ComputeStatisticsHistogramsResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(400)
            .Produces(404);

        // Legend endpoint - per-class swatches for the layer renderer
        group.MapGet("/legend", GetLegend)
            .WithDisplayName("Get Image Server Legend")
            .WithName("GetImageServerLegend")
            .WithSummary("Get raster legend swatches")
            .WithDescription("Returns Esri-compatible legend swatches for the layer's primary raster")
            .Produces<LegendResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(400)
            .Produces(404);

        // Analyze raster function chain - validates the renderingRule pipeline
        group.MapGet("/computeClass", AnalyzeFunctionChainGet)
            .WithDisplayName("Compute Class (GET)")
            .WithName("ImageServerComputeClassGet")
            .WithSummary("Validate a raster function chain")
            .WithDescription("Walks the supplied renderingRule and returns the executed function chain metadata")
            .Produces<AnalyzeResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(400)
            .Produces(404);

        group.MapPost("/computeClass", AnalyzeFunctionChainPost)
            .WithDisplayName("Compute Class (POST)")
            .WithName("ImageServerComputeClassPost")
            .WithSummary("Validate a raster function chain via POST")
            .WithDescription("POST equivalent of the GET computeClass endpoint")
            .Produces<AnalyzeResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(400)
            .Produces(404);
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

        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context,
            id,
            cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }

        return await handler.GetServiceInfoAsync(context, id, cancellationToken);
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
    {
        if (!IsSupportedExportResponseFormat(request.F))
        {
            return CreateUnsupportedExportFormatResult(context);
        }

        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context,
            id,
            cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
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
    {
        if (!IsSupportedJsonResponseFormat(request.F))
        {
            return CreateUnsupportedJsonFormatResult(context);
        }

        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context,
            id,
            cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
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
        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context,
            id,
            cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }

        var values = FeatureServerEndpoints.ToCaseInsensitiveDictionary(context.Request.Query);
        return await handler.QueryCatalogAsync(context, id, values, cancellationToken);
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
        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context,
            id,
            cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }

        var (bodyValues, readError) = await FeatureServerEndpoints.TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (bodyValues is null)
        {
            if (FeatureServerEndpoints.TryGetUnsupportedMediaType(readError, out var receivedContentType))
            {
                return FeatureServerEndpoints.CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
            }

            return StandardErrorHelpers.CreateBadRequest(context, readError ?? "Invalid request body.");
        }

        var merged = FeatureServerEndpoints.ToCaseInsensitiveDictionary(context.Request.Query);
        foreach (var pair in bodyValues)
        {
            merged[pair.Key] = pair.Value;
        }

        return await handler.QueryCatalogAsync(context, id, merged, cancellationToken);
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
        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context,
            id,
            cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }

        var values = FeatureServerEndpoints.ToCaseInsensitiveDictionary(context.Request.Query);
        return await handler.ComputeAsync(context, id, values, cancellationToken);
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
        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context,
            id,
            cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }

        var (bodyValues, readError) = await FeatureServerEndpoints.TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (bodyValues is null)
        {
            if (FeatureServerEndpoints.TryGetUnsupportedMediaType(readError, out var receivedContentType))
            {
                return FeatureServerEndpoints.CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
            }

            return StandardErrorHelpers.CreateBadRequest(context, readError ?? "Invalid request body.");
        }

        var merged = FeatureServerEndpoints.ToCaseInsensitiveDictionary(context.Request.Query);
        foreach (var pair in bodyValues)
        {
            merged[pair.Key] = pair.Value;
        }

        return await handler.ComputeAsync(context, id, merged, cancellationToken);
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

        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context,
            id,
            cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }

        return await handler.GetLegendAsync(context, id, cancellationToken);
    }

    /// <summary>
    /// Validate a raster function chain via GET (renderingRule supplied as URL parameter).
    /// </summary>
    private static async Task<IResult> AnalyzeFunctionChainGet(
        int id,
        HttpContext context,
        ImageServerAnalyzeHandler handler,
        CancellationToken cancellationToken = default)
    {
        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context,
            id,
            cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }

        var values = FeatureServerEndpoints.ToCaseInsensitiveDictionary(context.Request.Query);
        return await handler.AnalyzeAsync(context, id, values, cancellationToken);
    }

    /// <summary>
    /// Validate a raster function chain via POST (renderingRule supplied in body).
    /// </summary>
    private static async Task<IResult> AnalyzeFunctionChainPost(
        int id,
        HttpContext context,
        ImageServerAnalyzeHandler handler,
        CancellationToken cancellationToken = default)
    {
        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context,
            id,
            cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }

        var (bodyValues, readError) = await FeatureServerEndpoints.TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (bodyValues is null)
        {
            if (FeatureServerEndpoints.TryGetUnsupportedMediaType(readError, out var receivedContentType))
            {
                return FeatureServerEndpoints.CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
            }

            return StandardErrorHelpers.CreateBadRequest(context, readError ?? "Invalid request body.");
        }

        var merged = FeatureServerEndpoints.ToCaseInsensitiveDictionary(context.Request.Query);
        foreach (var pair in bodyValues)
        {
            merged[pair.Key] = pair.Value;
        }

        return await handler.AnalyzeAsync(context, id, merged, cancellationToken);
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
        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context,
            id,
            cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }

        return await handler.GetImageTileAsync(context, id, level, row, col, format, cancellationToken);
    }

    private static bool IsSupportedJsonResponseFormat(string? format)
        => string.IsNullOrWhiteSpace(format) ||
           string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(format, "pjson", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedExportResponseFormat(string? format)
        => IsSupportedJsonResponseFormat(format) ||
           string.Equals(format, InlineImageFormat, StringComparison.OrdinalIgnoreCase);

    private static IResult CreateUnsupportedJsonFormatResult(HttpContext context)
        => StandardErrorHelpers.CreateBadRequest(context, "Only JSON format is supported. Use f=json or f=pjson");

    private static IResult CreateUnsupportedExportFormatResult(HttpContext context)
        => StandardErrorHelpers.CreateBadRequest(context, "Only JSON and image formats are supported. Use f=json, f=pjson, or f=image");
}
