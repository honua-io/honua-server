// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Tiles;
using Honua.Server.Features.Infrastructure.Validation;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private static async Task<IResult> HandleLayerTile(
        int layerId,
        int z,
        int x,
        int y,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.Tiles, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var tileOptions = context.RequestServices.GetRequiredService<IOptions<TileOptions>>().Value;
        var tileLimits = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.Tiles;
        if (z < tileLimits.MinTileZoom || z > tileLimits.MaxTileZoom)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                $"Zoom level {z} is outside supported range",
                [$"Supported zoom range is {tileLimits.MinTileZoom}-{tileLimits.MaxTileZoom}"]);
        }

        var maxIndex = 1 << z;
        if (x < 0 || y < 0 || x >= maxIndex || y >= maxIndex)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                $"Invalid tile coordinates: x={x}, y={y}, z={z}");
        }

        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context,
            layerId,
            requiredProtocol: ServiceProtocols.FeatureServer,
            cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }
        var layer = layerValidation.Layer!;

        var where = GetValueString(ToCaseInsensitiveDictionary(context.Request.Query), "where");
        SqlFragment? sqlFilter = null;
        if (!string.IsNullOrWhiteSpace(where))
        {
            var filterService = context.RequestServices.GetRequiredService<IFilterExpressionService>();
            var parseResult = filterService.Parse(FilterLanguage.ArcGisSql, where);
            if (!parseResult.IsSuccess)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    ErrorMessages.Validation.InvalidParameter,
                    [parseResult.ErrorMessage ?? "Invalid filter syntax."]);
            }

            if (parseResult.Expression != null)
            {
                var translationResult = filterService.Translate(parseResult.Expression, layer);
                if (!translationResult.IsSuccess)
                {
                    return StandardErrorHelpers.CreateBadRequest(context,
                        ErrorMessages.Validation.InvalidParameter,
                        [translationResult.ErrorMessage ?? "Invalid filter syntax."]);
                }

                sqlFilter = translationResult.SqlFilter;
            }
        }
        var query = VectorTileExecution.CreateQuery(
            layer.SpatialReference.ToSrid(),
            where,
            sqlFilter);

        var tileProvider = context.RequestServices.GetRequiredService<ITileProvider>();
        return await VectorTileExecution.ExecuteAsync(
            context,
            tileProvider,
            layer,
            x,
            y,
            z,
            query,
            tileOptions,
            tileLimits,
            cancellationToken);
    }
}
