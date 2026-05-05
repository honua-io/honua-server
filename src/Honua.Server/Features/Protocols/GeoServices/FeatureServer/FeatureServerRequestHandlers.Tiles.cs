// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Rendering;
using Honua.Server.Features.Infrastructure.Validation;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer;

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

        var queryValues = ToCaseInsensitiveDictionary(context.Request.Query);
        var where = GetValueString(queryValues, "where");
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

        TemporalFilter? temporalFilter = null;
        var timeParam = GetValueString(queryValues, "time");
        if (!string.IsNullOrWhiteSpace(timeParam))
        {
            // Parse first so the documented time=null,null no-op behaves like an
            // omitted parameter — neither the Pro gate nor temporal-field
            // resolution should fire when no actual filter is requested.
            if (!TryBuildTileTemporalFilter(layer, timeParam, out temporalFilter, out var temporalError))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid time parameter",
                    [temporalError ?? "Invalid time parameter."]);
            }

            if (temporalFilter is not null)
            {
                var editionError = RequireProEditionForTimeSeriesTiles(context);
                if (editionError != null)
                {
                    return editionError;
                }
            }
        }

        var query = VectorTileExecution.CreateQuery(
            layer.SpatialReference.ToSrid(),
            where,
            sqlFilter,
            temporalFilter);

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

    private static IResult? RequireProEditionForTimeSeriesTiles(HttpContext context)
    {
        var licenseProvider = context.RequestServices.GetRequiredService<ILicenseStatusProvider>();
        var edition = licenseProvider.GetCurrentStatus().Edition;
        if (edition >= HonuaEdition.Pro)
        {
            return null;
        }

        return StandardErrorHelpers.CreateForbidden(
            context,
            $"Time-filtered vector tiles require the Pro edition or higher. Current edition: {edition}.");
    }

    private static bool TryBuildTileTemporalFilter(
        LayerDefinition layer,
        string timeParam,
        out TemporalFilter? temporalFilter,
        out string? error)
    {
        temporalFilter = null;
        error = null;

        // Parse before resolving temporal fields so time=null,null (the
        // documented no-op) does not require the layer to be time-aware.
        if (!GeoServicesTemporalQueryBuilder.TryParseTimeParameter(timeParam, out var start, out var end))
        {
            error = $"Invalid time parameter format: {timeParam}";
            return false;
        }

        if (start is null && end is null)
        {
            return true;
        }

        TemporalExtentHelpers.TemporalFieldSelection selection;
        try
        {
            selection = TemporalExtentHelpers.ResolveTemporalFieldsOrThrow(layer);
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }

        temporalFilter = new TemporalFilter
        {
            PropertyName = selection.StartField.Name,
            PropertyType = selection.StartField.Type == FieldType.Date
                ? TemporalPropertyType.Date
                : TemporalPropertyType.DateTime,
            Start = start,
            End = end
        };
        return true;
    }
}
