// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Configuration;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Api.Tiles.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.Ogc.Api.Tiles;

internal static class TileMatrixSetEndpoints
{
    public static IEndpointRouteBuilder MapTileMatrixSetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var tileMatrixSets = endpoints.MapGet("/ogc/tiles/tileMatrixSets", HandleGetTileMatrixSets)
            .WithDisplayName("OGC API Tiles TileMatrixSets")
            .WithName("OgcTilesTileMatrixSets")
            .WithSummary("Get available tile matrix sets")
            .WithDescription("Lists supported tile matrix sets")
            .WithTags("OGC API Tiles")
            .CacheOutput("OgcTilesTileMatrixSets")
            .Produces<TileMatrixSetsList>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html);

        var tileMatrixSet = endpoints.MapGet("/ogc/tiles/tileMatrixSets/{tileMatrixSetId}", HandleGetTileMatrixSet)
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
        var validationError = OgcCommonUtilities.ValidateQueryParameters(context.Request, OgcTilesUtilities.AllowedQueryParameters.Metadata);
        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!OgcCommonUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
        {
            return OgcCommonUtilities.CreateFormatError(context, formatError);
        }

        var request = context.Request;
        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var items = ImmutableArray.Create(
            OgcTilesUtilities.BuildWebMercatorQuadItem(baseUrl),
            OgcTilesUtilities.BuildWorldCrs84QuadItem(baseUrl));

        var links = OgcCommonUtilities.BuildFormatLinks(
                request,
                $"{baseUrl}/ogc/tiles/tileMatrixSets",
                outputFormat,
                OgcCommonUtilities.MetadataFormats,
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

        return OgcCommonUtilities.FormatMetadataResponse(response, OgcTilesJsonContext.Default.TileMatrixSetsList, outputFormat, "Tile matrix sets");
    }

    private static IResult HandleGetTileMatrixSet(
        string tileMatrixSetId,
        HttpContext context,
        string? f,
        [FromServices] IOptions<LimitsOptions> limitsOptions)
    {
        var validationError = OgcCommonUtilities.ValidateQueryParameters(context.Request, OgcTilesUtilities.AllowedQueryParameters.Metadata);
        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!OgcCommonUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
        {
            return OgcCommonUtilities.CreateFormatError(context, formatError);
        }

        if (!OgcTilesUtilities.IsSupportedTileMatrixSet(tileMatrixSetId))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Tile matrix set '{tileMatrixSetId}' not found.");
        }

        var definition = OgcTilesUtilities.IsWorldCrs84Quad(tileMatrixSetId)
            ? OgcTilesUtilities.BuildWorldCrs84QuadDefinition(limitsOptions.Value.Tiles)
            : OgcTilesUtilities.BuildWebMercatorQuadDefinition(limitsOptions.Value.Tiles);
        return OgcCommonUtilities.FormatMetadataResponse(definition, OgcTilesJsonContext.Default.TileMatrixSetDefinition, outputFormat, "Tile matrix set");
    }
}
