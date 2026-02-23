// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.MapServer.Models;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.MapServer;

internal static partial class MapServerEndpoints
{
    private const int MaxFindResults = 1000;

    /// <summary>
    /// Handle MapServer find (cross-layer text search) requests.
    /// </summary>
    private static async Task<IResult> HandleFind(HttpContext context)
    {
        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Honua.Server.MapServerEndpoints");

        try
        {
            var (values, readError) = await TryReadMapServerRequestValuesAsync(context);
            if (values == null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, readError ?? "Invalid request body.");
            }

            var searchText = GetValue(values, "searchText");
            if (string.IsNullOrWhiteSpace(searchText))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "searchText parameter is required.");
            }

            var layersParam = GetValue(values, "layers");
            if (string.IsNullOrWhiteSpace(layersParam))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "layers parameter is required.");
            }

            var containsValue = GetValue(values, "contains");
            var contains = string.IsNullOrWhiteSpace(containsValue) ||
                           !string.Equals(containsValue, "false", StringComparison.OrdinalIgnoreCase);

            var searchFieldsParam = GetValue(values, "searchFields");
            var srValue = GetValue(values, "sr");
            var layerDefsValue = GetValue(values, "layerDefs");
            var returnGeometry = !string.Equals(GetValue(values, "returnGeometry"), "false", StringComparison.OrdinalIgnoreCase);
            var responseFormat = GetValue(values, "f") ?? "json";

            if (!string.Equals(responseFormat, "json", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(responseFormat, "pjson", StringComparison.OrdinalIgnoreCase))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    $"Output format '{responseFormat}' is not supported.");
            }

            var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
            var serviceResult = await resourceValidator.ValidateServiceAsync(serviceId, context.RequestAborted);
            if (!serviceResult.IsValid)
            {
                var errorMessage = serviceResult.ErrorMessage ?? "Service not found.";
                if (serviceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
                {
                    return StandardErrorHelpers.CreateBadRequest(context, errorMessage);
                }

                return StandardErrorHelpers.CreateNotFound(context, errorMessage);
            }

            var service = serviceResult.Resource!;
            var protocolError = ProtocolValidationHelpers.ValidateProtocolEnabled(context, service, ServiceProtocols.MapServer);
            if (protocolError is not null)
            {
                return protocolError;
            }

            var gdbVersion = GetValue(values, "gdbVersion");
            if (!string.IsNullOrWhiteSpace(gdbVersion))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "gdbVersion is not supported.");
            }

            var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
            if (!TryParseLayerDefs(layerDefsValue, queryValidator, out var layerDefs, out var layerDefsError))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    layerDefsError ?? "Invalid layerDefs parameter.");
            }

            if (!TryParseDynamicLayers(GetValue(values, "dynamicLayers"), service, queryValidator, out var dynamicLayers, out var dynamicLayersError))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    dynamicLayersError ?? "Invalid dynamicLayers parameter.");
            }

            var requestedLayerIds = ParseLayerIds(layersParam);
            if (requestedLayerIds.Count == 0)
            {
                return StandardErrorHelpers.CreateBadRequest(context, "layers parameter must specify at least one layer id.");
            }

            var searchFieldNames = ParseSearchFields(searchFieldsParam);

            var outputSrid = service.SpatialReference.Srid;
            if (!string.IsNullOrWhiteSpace(srValue))
            {
                var parsed = TryParseSrid(srValue);
                if (!parsed.HasValue)
                {
                    return StandardErrorHelpers.CreateBadRequest(context, "Invalid sr parameter.");
                }

                outputSrid = parsed.Value;
            }

            MapServerLog.FindRequested(logger, serviceId, searchText);

            using var activity = HonuaTelemetry.ActivitySource.StartActivity(
                "MapServerFind", ActivityKind.Internal);
            activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.MapServer);
            activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
            activity?.SetTag(HonuaTelemetry.Tags.Operation, "find");

            var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
            var geometryConverter = context.RequestServices.GetRequiredService<IGeometryConverter>();
            var filterExpressionService = context.RequestServices.GetRequiredService<IFilterExpressionService>();

            var results = new List<FindResult>();

            var findLayers = ResolveFindLayers(service, requestedLayerIds, dynamicLayers, context);

            foreach (var (layer, definitionExpression) in findLayers)
            {
                if (!AccessPolicyHelpers.IsLayerAccessible(context, layer, service))
                {
                    continue;
                }

                var fieldsToSearch = ResolveSearchFields(layer, searchFieldNames);
                if (fieldsToSearch.Length == 0)
                {
                    continue;
                }

                layerDefs.TryGetValue(layer.Id, out var rawLayerDef);
                var layerDef = CombineDefinitionExpressions(definitionExpression, rawLayerDef);

                var objectIdField = layer.PrimaryKeyField?.Name ?? FieldNames.ObjectId;
                var displayField = ResolveDisplayField(layer, objectIdField);

                foreach (var field in fieldsToSearch)
                {
                    var escapedSearchText = EscapeSqlStringLiteral(searchText);
                    var quotedFieldName = $"\"{field.Name}\"";
                    var whereClause = contains
                        ? $"{quotedFieldName} LIKE '%{EscapeLikeWildcards(escapedSearchText)}%' ESCAPE '\\'"
                        : $"{quotedFieldName} = '{escapedSearchText}'";

                    if (!string.IsNullOrWhiteSpace(layerDef))
                    {
                        whereClause = $"({layerDef}) AND ({whereClause})";
                    }

                    var translationResult = filterExpressionService.Translate(FilterLanguage.ArcGisSql, whereClause, layer);
                    if (!translationResult.IsSuccess)
                    {
                        continue;
                    }

                    var featureQuery = new FeatureQuery
                    {
                        SpatialReferenceSrid = service.SpatialReference.Srid,
                        OutputSrid = outputSrid,
                        Limit = MaxFindResults - results.Count,
                        SqlFilter = translationResult.SqlFilter
                    };

                    var queryResult = await featureReader.QueryAsync(layer.Id, featureQuery, context.RequestAborted);

                    foreach (var feature in queryResult.Items)
                    {
                        var attributes = new Dictionary<string, object?>();
                        foreach (var kvp in feature.Attributes)
                        {
                            attributes[kvp.Key] = kvp.Value;
                        }

                        object? geometryResult = null;
                        if (returnGeometry && feature.Geometry != null)
                        {
                            try
                            {
                                geometryResult = geometryConverter.ConvertWkbToGeoServicesGeometry(feature.Geometry, outputSrid);
                            }
                            catch (ArgumentException)
                            {
                                geometryResult = null;
                            }
                        }

                        var displayValue = GetDisplayFieldValue(feature, displayField);

                        results.Add(new FindResult
                        {
                            LayerId = layer.Id,
                            LayerName = layer.Name,
                            DisplayFieldName = displayField,
                            FoundFieldName = field.Name,
                            Value = displayValue,
                            Attributes = attributes,
                            GeometryType = layer.HasGeometry ? MapGeometryTypeToEsri(layer.GeometryType) : null,
                            Geometry = geometryResult
                        });
                    }

                    if (results.Count >= MaxFindResults)
                    {
                        break;
                    }
                }

                if (results.Count >= MaxFindResults)
                {
                    break;
                }
            }

            MapServerLog.FindCompleted(logger, serviceId, results.Count);
            HonuaTelemetry.SetSuccess(activity, results.Count);

            var response = new FindResponse { Results = [.. results] };
            return Results.Json(response, MapServerJsonContext.Default.FindResponse, contentType: "application/json");
        }
        catch (ArgumentException ex)
        {
            MapServerLog.FindFailed(logger, serviceId, ex.Message, ex);
            return StandardErrorHelpers.CreateBadRequest(context, ex.Message);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MapServerLog.FindFailed(logger, serviceId, ex.Message, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "MapServer find failed.");
        }
    }

    private static HashSet<string>? ParseSearchFields(string? searchFieldsParam)
    {
        if (string.IsNullOrWhiteSpace(searchFieldsParam))
        {
            return null;
        }

        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in searchFieldsParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            fields.Add(part);
        }

        return fields.Count > 0 ? fields : null;
    }

    private static FieldDefinition[] ResolveSearchFields(LayerDefinition layer, HashSet<string>? requestedFields)
    {
        if (requestedFields is { Count: > 0 })
        {
            return layer.AttributeFields
                .Where(f => f.Type == FieldType.String && requestedFields.Contains(f.Name))
                .ToArray();
        }

        return layer.AttributeFields
            .Where(f => f.Type == FieldType.String)
            .ToArray();
    }

    private static List<(LayerDefinition Layer, string? DefinitionExpression)> ResolveFindLayers(
        ServiceDefinition service,
        HashSet<int> requestedLayerIds,
        IReadOnlyList<DynamicLayerDefinition> dynamicLayers,
        HttpContext context)
    {
        if (dynamicLayers.Count > 0)
        {
            var layerLookup = service.Layers.ToDictionary(l => l.Id);
            var result = new List<(LayerDefinition, string?)>();

            foreach (var dl in dynamicLayers)
            {
                if (!requestedLayerIds.Contains(dl.Id))
                {
                    continue;
                }

                if (!layerLookup.TryGetValue(dl.MapLayerId, out var layer))
                {
                    continue;
                }

                result.Add((layer, dl.DefinitionExpression));
            }

            return result;
        }

        return service.Layers
            .Where(l => requestedLayerIds.Contains(l.Id))
            .Select(l => (l, (string?)null))
            .ToList();
    }

    /// <summary>
    /// Escapes single quotes for SQL string literals ('' escaping).
    /// </summary>
    private static string EscapeSqlStringLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>
    /// Escapes LIKE wildcard characters (% and _) in a value that is already
    /// quote-escaped, so they are treated as literal characters.
    /// </summary>
    private static string EscapeLikeWildcards(string value)
        => value.Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
