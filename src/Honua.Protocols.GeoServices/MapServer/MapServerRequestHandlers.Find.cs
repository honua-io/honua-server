// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Collections.Immutable;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Protocols.GeoServices.MapServer.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;
using static Honua.Server.Features.Infrastructure.Helpers.DelimitedParameterHelpers;

namespace Honua.Protocols.GeoServices.MapServer;

internal static partial class MapServerEndpoints
{
    private const string InvalidFindRequestMessage = "Invalid find request parameters.";

    private sealed record FindLayerDescriptor(
        int PublicLayerId,
        int StorageLayerId,
        string Name,
        MetadataV2Resource Resource,
        string? DefinitionExpression);

    /// <summary>
    /// Handle MapServer find (cross-layer text search) requests.
    /// </summary>
    private static async Task<IResult> HandleFind(HttpContext context)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Honua.Server.MapServerEndpoints");
        using var scope = HonuaTelemetryScope.StartFeature(
            "find",
            HonuaTelemetry.Protocols.MapServer,
            "*");
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId)
            .WithTag(HonuaTelemetry.Tags.Operation, "find");

        try
        {
            var earlyServiceError = await TryValidateMapServerServiceAsync(serviceId, context);
            if (earlyServiceError is not null)
            {
                return earlyServiceError;
            }

            var (values, readError) = await TryReadMapServerRequestValuesAsync(context);
            if (values == null)
            {
                if (GeoServicesRequestValueHelpers.TryGetUnsupportedMediaType(readError, out var receivedContentType))
                {
                    return GeoServicesRequestValueHelpers.CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
                }

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

            if (HasEmptyCommaSeparatedToken(layersParam))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "layers parameter must specify at least one layer id.");
            }

            if (HasNonIntegerLayerToken(layersParam))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "layers parameter must contain integer layer ids.");
            }

            var containsValue = GetValue(values, "contains");
            var contains = string.IsNullOrWhiteSpace(containsValue) ||
                           !string.Equals(containsValue, "false", StringComparison.OrdinalIgnoreCase);

            var searchFieldsParam = GetValue(values, "searchFields");
            if (HasEmptyCommaSeparatedToken(searchFieldsParam))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "searchFields parameter contains an empty field name.");
            }

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
            var serviceResult = await ServiceResourceValidationHelpers.ValidateServiceV2Async(
                resourceValidator,
                serviceId,
                ServiceProtocols.MapServer,
                context,
                cancellationToken: cancellationToken);
            if (!serviceResult.IsValid)
            {
                return serviceResult.ErrorResult!;
            }

            var service = serviceResult.Service!;
            var graphProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var publishedLayers = ResolveFindPublishedLayers(snapshot, service);

            var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
            if (!TryParseLayerDefs(layerDefsValue, queryValidator, out var layerDefs, out var layerDefsError))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    layerDefsError ?? "Invalid layerDefs parameter.");
            }

            var knownLayerIds = publishedLayers
                .Select(static layer => layer.PublicLayerId)
                .ToHashSet();
            if (!TryParseDynamicLayers(GetValue(values, "dynamicLayers"), knownLayerIds, queryValidator, out var dynamicLayers, out var dynamicLayersError))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    dynamicLayersError ?? "Invalid dynamicLayers parameter.");
            }

            scope.WithTag(HonuaTelemetry.Tags.LayerId, layersParam.Trim());

            var requestedLayerIds = ParseLayerIds(layersParam);
            if (requestedLayerIds.Count == 0)
            {
                return StandardErrorHelpers.CreateBadRequest(context, "layers parameter must specify at least one layer id.");
            }

            var searchFieldNames = ParseSearchFields(searchFieldsParam);

            var outputSrid = service.SpatialReference?.ResolveSrid() ?? SpatialReference.WGS84.Wkid;
            if (!string.IsNullOrWhiteSpace(srValue))
            {
                var parsed = SpatialReferenceHelpers.TryParseSrid(srValue);
                if (!parsed.HasValue)
                {
                    return StandardErrorHelpers.CreateBadRequest(context, "Invalid sr parameter.");
                }

                outputSrid = parsed.Value;
            }

            MapServerLog.FindRequested(logger, serviceId, searchText);
            var stopwatch = Stopwatch.StartNew();

            var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
            var geometryConverter = context.RequestServices.GetRequiredService<IGeometryConverter>();
            var filterExpressionService = context.RequestServices.GetRequiredService<IFilterExpressionService>();
            var maxFindResults = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.Query.MaxRecordCount;

            var results = new List<FindResult>();

            var findLayers = ResolveFindLayers(publishedLayers, requestedLayerIds, dynamicLayers);
            var accessError = AccessPolicyHelpers.RequireAnyResourceAccess(
                context,
                findLayers.Select(static entry => entry.Resource),
                service);
            if (accessError != null)
            {
                return accessError;
            }

            foreach (var layer in findLayers)
            {
                if (results.Count >= maxFindResults)
                {
                    break;
                }

                if (!AccessPolicyHelpers.IsResourceAccessible(context, layer.Resource, service))
                {
                    continue;
                }

                var fieldsToSearch = ResolveSearchFields(layer.Resource, searchFieldNames);
                if (fieldsToSearch.Length == 0)
                {
                    continue;
                }

                layerDefs.TryGetValue(layer.PublicLayerId, out var rawLayerDef);
                var layerDef = CombineDefinitionExpressions(layer.DefinitionExpression, rawLayerDef);

                var objectIdField = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(layer.Resource);
                var displayField = ResolveDisplayField(layer.Resource, objectIdField);

                SqlFragment? layerSqlFilter = null;
                if (!string.IsNullOrWhiteSpace(layerDef))
                {
                    var parseResult = filterExpressionService.Parse(FilterLanguage.ArcGisSql, layerDef);
                    if (!parseResult.IsSuccess)
                    {
                        continue;
                    }

                    var translationResult = filterExpressionService.Translate(parseResult.Expression, layer.Resource);
                    if (!translationResult.IsSuccess)
                    {
                        continue;
                    }

                    layerSqlFilter = translationResult.SqlFilter;
                }

                var outFields = fieldsToSearch
                    .Select(field => field.Name)
                    .Append(displayField)
                    .Append(objectIdField)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToImmutableArray();

                var pageSize = Math.Clamp(Math.Max(maxFindResults * 10, 50), 50, 500);
                var offset = 0;

                while (results.Count < maxFindResults)
                {
                    var featureQuery = new FeatureQuery
                    {
                        SpatialReferenceSrid = layer.Resource.ReadSrid() ?? SpatialReference.WGS84.Wkid,
                        OutputSrid = outputSrid,
                        Limit = pageSize,
                        Offset = offset,
                        OutFields = outFields,
                        SqlFilter = layerSqlFilter
                    };

                    var queryResult = await featureReader.QueryAsync(layer.StorageLayerId, featureQuery, cancellationToken);
                    if (queryResult.Items.Length == 0)
                    {
                        break;
                    }

                    foreach (var feature in queryResult.Items)
                    {
                        var attributes = new Dictionary<string, object?>();
                        foreach (var kvp in feature.Attributes)
                        {
                            if (FeatureAttributeVisibility.IsInternalAttribute(kvp.Key))
                            {
                                continue;
                            }

                            attributes[kvp.Key] = FeatureAttributeValueNormalizer.Normalize(kvp.Value);
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
                        foreach (var field in fieldsToSearch)
                        {
                            if (!TryMatchSearchField(feature, field, searchText, contains))
                            {
                                continue;
                            }

                            results.Add(new FindResult
                            {
                                LayerId = layer.PublicLayerId,
                                LayerName = layer.Name,
                                DisplayFieldName = displayField,
                                FoundFieldName = field.Name,
                                Value = displayValue,
                                Attributes = attributes,
                                GeometryType = HasFindGeometry(layer.Resource) ? MapGeometryTypeToEsri(layer.Resource.ReadGeometryType()) : null,
                                Geometry = geometryResult
                            });

                            if (results.Count >= maxFindResults)
                            {
                                break;
                            }
                        }

                        if (results.Count >= maxFindResults)
                        {
                            break;
                        }
                    }

                    if (queryResult.Items.Length < pageSize)
                    {
                        break;
                    }

                    offset += queryResult.Items.Length;
                }
            }

            MapServerLog.FindCompleted(logger, serviceId, results.Count);
            stopwatch.Stop();
            scope.SetSuccess(results.Count);
            scope.CategorizeLatency(stopwatch.Elapsed.TotalMilliseconds);

            var response = new FindResponse { Results = [.. results] };
            return Results.Json(response, MapServerJsonContext.Default.FindResponse, contentType: "application/json");
        }
        catch (ArgumentException ex)
        {
            MapServerLog.FindFailed(logger, serviceId, ex.Message, ex);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateBadRequest(context, InvalidFindRequestMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MapServerLog.FindFailed(logger, serviceId, ex.Message, ex);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "MapServer find failed.");
        }
    }

    private static FindLayerDescriptor[] ResolveFindPublishedLayers(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service)
    {
        var descriptors = new List<FindLayerDescriptor>();
        foreach (var publication in snapshot.Index.PublicationsByService[service.Metadata.Id])
        {
            if (publication.LayerIndex is not int publicLayerId)
            {
                continue;
            }

            var resource = snapshot.ResolveResource(publication);
            if (resource is null)
            {
                continue;
            }

            var storageLayerId = snapshot.ResolveStorageLayerId(publication)
                ?? snapshot.ResolveStorageLayerId(resource)
                ?? publicLayerId;
            var layerName = string.IsNullOrWhiteSpace(resource.Metadata.Name)
                ? publication.Metadata.Name
                : resource.Metadata.Name;

            descriptors.Add(new FindLayerDescriptor(
                publicLayerId,
                storageLayerId,
                string.IsNullOrWhiteSpace(layerName)
                    ? publicLayerId.ToString(CultureInfo.InvariantCulture)
                    : layerName,
                resource,
                null));
        }

        return [.. descriptors.OrderBy(static layer => layer.PublicLayerId)];
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

    private static bool HasNonIntegerLayerToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var token in value.Split(',', StringSplitOptions.None))
        {
            var trimmed = token.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                return true;
            }
        }

        return false;
    }

    private static MetadataV2Field[] ResolveSearchFields(MetadataV2Resource resource, HashSet<string>? requestedFields)
    {
        if (requestedFields is { Count: > 0 })
        {
            return resource.SchemaFields
                .Where(f => f.Type == MetadataV2FieldType.String && requestedFields.Contains(f.Name))
                .ToArray();
        }

        return resource.SchemaFields
            .Where(f => f.Type == MetadataV2FieldType.String)
            .ToArray();
    }

    private static List<FindLayerDescriptor> ResolveFindLayers(
        IReadOnlyList<FindLayerDescriptor> publishedLayers,
        HashSet<int> requestedLayerIds,
        IReadOnlyList<DynamicLayerDefinition> dynamicLayers)
    {
        if (dynamicLayers.Count > 0)
        {
            var layerLookup = publishedLayers.ToDictionary(static layer => layer.PublicLayerId);
            var result = new List<FindLayerDescriptor>();

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

                result.Add(layer with { DefinitionExpression = dl.DefinitionExpression });
            }

            return result;
        }

        return publishedLayers
            .Where(l => requestedLayerIds.Contains(l.PublicLayerId))
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

    private static bool TryMatchSearchField(
        Feature feature,
        MetadataV2Field field,
        string searchText,
        bool contains)
    {
        if (!TryGetAttributeValue(feature, field.Name, out var value) || value is null)
        {
            return false;
        }

        var actualText = value.ToString();
        if (string.IsNullOrEmpty(actualText))
        {
            return false;
        }

        return contains
            ? actualText.Contains(searchText, StringComparison.OrdinalIgnoreCase)
            : string.Equals(actualText, searchText, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetAttributeValue(Feature feature, string fieldName, out object? value)
    {
        if (feature.Attributes.TryGetValue(fieldName, out value))
        {
            return true;
        }

        foreach (var attribute in feature.Attributes)
        {
            if (string.Equals(attribute.Key, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                value = attribute.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool HasFindGeometry(MetadataV2Resource resource)
    {
        var geometryType = resource.ReadGeometryType();
        return geometryType != MetadataV2GeometryType.None ||
               resource.FindPrimaryGeometryField() is not null;
    }

    private static string? MapGeometryTypeToEsri(MetadataV2GeometryType geometryType)
    {
        return geometryType switch
        {
            MetadataV2GeometryType.Point => "esriGeometryPoint",
            MetadataV2GeometryType.MultiPoint => "esriGeometryMultipoint",
            MetadataV2GeometryType.LineString or
            MetadataV2GeometryType.MultiLineString => "esriGeometryPolyline",
            MetadataV2GeometryType.Polygon or
            MetadataV2GeometryType.MultiPolygon => "esriGeometryPolygon",
            _ => null
        };
    }
}
