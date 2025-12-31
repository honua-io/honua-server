// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.OgcFeatures;
using Honua.Server.Features.OgcFeatures.Models;
using Honua.Server.Features.OgcTiles.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.OgcTiles;

internal static class TileMatrixSetEndpoints
{
    public static IEndpointRouteBuilder MapTileMatrixSetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/ogc/tiles/tileMatrixSets", HandleGetTileMatrixSets)
            .WithDisplayName("OGC API Tiles TileMatrixSets")
            .WithName("OgcTilesTileMatrixSets")
            .WithSummary("Get available tile matrix sets")
            .WithDescription("Lists supported tile matrix sets")
            .WithTags("OGC API Tiles")
            .CacheOutput("OgcTilesTileMatrixSets")
            .Produces<TileMatrixSetsList>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html);

        endpoints.MapGet("/ogc/tiles/tileMatrixSets/{tileMatrixSetId}", HandleGetTileMatrixSet)
            .WithDisplayName("OGC API Tiles TileMatrixSet")
            .WithName("OgcTilesTileMatrixSet")
            .WithSummary("Get tile matrix set definition")
            .WithDescription("Returns a tile matrix set definition")
            .WithTags("OGC API Tiles")
            .CacheOutput("OgcTilesTileMatrixSet")
            .Produces<TileMatrixSetDefinition>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html);

        return endpoints;
    }

    private static IResult HandleGetTileMatrixSets(HttpContext context, string? f)
    {
        var validationError = OgcFeaturesUtilities.ValidateQueryParameters(context.Request, OgcTilesUtilities.AllowedQueryParameters.Metadata);
        if (validationError is not null)
        {
            return OgcErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!OgcFeaturesUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
        {
            return CreateFormatError(context, formatError);
        }

        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";
        var items = ImmutableArray.Create(OgcTilesUtilities.BuildWebMercatorQuadItem(baseUrl));

        var links = OgcFeaturesUtilities.BuildFormatLinks(
                request,
                $"{baseUrl}/ogc/tiles/tileMatrixSets",
                outputFormat,
                OgcFeaturesUtilities.MetadataFormats,
                "Tile matrix sets")
            .ToBuilder();

        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/tiles",
            rel: "parent",
            type: MediaTypes.Json,
            title: "Landing page"));

        var response = new TileMatrixSetsList
        {
            TileMatrixSets = items,
            Links = links.ToImmutable()
        };

        return OgcFeaturesUtilities.FormatMetadataResponse(response, OgcTilesJsonContext.Default.TileMatrixSetsList, outputFormat, "Tile matrix sets");
    }

    private static IResult HandleGetTileMatrixSet(
        string tileMatrixSetId,
        HttpContext context,
        string? f,
        IOptions<TileOptions> tileOptions)
    {
        var validationError = OgcFeaturesUtilities.ValidateQueryParameters(context.Request, OgcTilesUtilities.AllowedQueryParameters.Metadata);
        if (validationError is not null)
        {
            return OgcErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!OgcFeaturesUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
        {
            return CreateFormatError(context, formatError);
        }

        if (!OgcTilesUtilities.IsSupportedTileMatrixSet(tileMatrixSetId))
        {
            return OgcErrorHelpers.CreateNotFound(context, $"Tile matrix set '{tileMatrixSetId}' not found.");
        }

        var definition = OgcTilesUtilities.BuildWebMercatorQuadDefinition(tileOptions.Value);
        return OgcFeaturesUtilities.FormatMetadataResponse(definition, OgcTilesJsonContext.Default.TileMatrixSetDefinition, outputFormat, "Tile matrix set");
    }

    private static IResult CreateFormatError(HttpContext context, IResult? formatError)
    {
        if (formatError is BadRequest<string> badRequest)
        {
            return OgcErrorHelpers.CreateBadRequest(context, badRequest.Value ?? "Invalid format.");
        }

        if (formatError is IStatusCodeHttpResult statusCodeResult && statusCodeResult.StatusCode.HasValue)
        {
            return ProtocolErrorWriter.CreateErrorResult(
                context,
                statusCodeResult.StatusCode.Value,
                "Not Acceptable",
                "Requested format is not acceptable.");
        }

        return OgcErrorHelpers.CreateBadRequest(context, "Invalid format.");
    }
}
