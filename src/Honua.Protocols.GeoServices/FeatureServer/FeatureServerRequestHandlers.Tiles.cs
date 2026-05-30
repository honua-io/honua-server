// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Rendering;
using Honua.Infrastructure.Validation;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private const string FeatureServerProtocolName = "FeatureServer";

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

        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessV2Async(
            context,
            layerId,
            requiredProtocol: FeatureServerProtocolName,
            cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }
        var publication = layerValidation.Publication!;
        var resource = layerValidation.Resource!;

        var snapshotProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await snapshotProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        // Mirror the V2 metadata builders' resolution order
        // (FeatureServerUtilities.V2.MapLayerInfoV2): integer storage handle is
        // publication.LayerIndex when the graph doesn't carry an explicit
        // storage binding for this publication.
        var storageLayerId = publication.LayerIndex ?? snapshot.ResolveStorageLayerId(publication);
        if (storageLayerId is null)
        {
            return StandardErrorHelpers.CreateNotFound(
                context,
                $"Layer '{resource.Metadata.Name ?? layerId.ToString(System.Globalization.CultureInfo.InvariantCulture)}' is not bound to a storage layer.");
        }

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
                var translationResult = filterService.Translate(parseResult.Expression, resource);
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
            if (!TryBuildTileTemporalFilterV2(resource, timeParam, out temporalFilter, out var temporalError))
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
            resource.ReadSrid() ?? SpatialReference.WGS84.Wkid,
            where,
            sqlFilter,
            temporalFilter);

        var tileProvider = context.RequestServices.GetRequiredService<ITileProvider>();
        try
        {
            return await VectorTileExecution.ExecuteAsync(
                context,
                tileProvider,
                storageLayerId.Value,
                x,
                y,
                z,
                query,
                tileOptions,
                tileLimits,
                cancellationToken);
        }
        catch (NotSupportedException) when (temporalFilter is not null)
        {
            // The configured feature provider (e.g., MySQL/MariaDB, SQL Server)
            // does not support TemporalFilter SQL translation. Surface as a
            // protocol-level 400 instead of letting the global error handler
            // map it to 500, so MVT clients can distinguish "this layer's
            // store can't filter by time" from "server failed".
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Temporal filtering is not supported",
                ["Time-filtered vector tiles are not supported by the configured feature provider for this layer."]);
        }
    }

    private static IResult? RequireProEditionForTimeSeriesTiles(HttpContext context)
    {
        return LicenseGate.RequireEntitlement(
            context,
            "temporal.time-series-tiles",
            "Time-filtered vector tiles");
    }

    /// <summary>
    /// V2 overload of the time-parameter helper. Resolves the opt-in temporal
    /// fields off <see cref="MetadataV2Resource"/> and infers the property type
    /// (Date vs DateTime) from the resolved schema field's
    /// <see cref="MetadataV2FieldType"/> entry.
    /// </summary>
    private static bool TryBuildTileTemporalFilterV2(
        MetadataV2Resource resource,
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

        // Strict opt-in: a non-empty time= filter requires explicit
        // resource.Temporal.StartTimeField (and any configured EndTimeField) to
        // resolve against real Date/DateTime/Time attributes on the resource
        // schema. Falling back to the first temporal attribute would silently
        // filter on a non-temporal column (calls out in
        // docs/gis/temporal-animation-api.md and matches GeoServices REST
        // query?time= rejection on non-time-aware layers).
        if (!TemporalExtentHelpers.TryResolveOptInTemporalFieldsV2(resource, out var selection))
        {
            error = $"Layer '{resource.Metadata.Name}' is not configured as time-aware.";
            return false;
        }

        var startFieldType = ResolveSchemaFieldType(resource, selection.StartFieldName);

        temporalFilter = new TemporalFilter
        {
            PropertyName = selection.StartFieldName,
            PropertyType = startFieldType == MetadataV2FieldType.Date
                ? TemporalPropertyType.Date
                : TemporalPropertyType.DateTime,
            EndPropertyName = selection.EndFieldName,
            Start = start,
            End = end
        };
        return true;
    }

    private static MetadataV2FieldType ResolveSchemaFieldType(MetadataV2Resource resource, string fieldName)
    {
        foreach (var field in resource.SchemaFields)
        {
            if (string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                return field.Type;
            }
        }
        return MetadataV2FieldType.DateTime;
    }
}
