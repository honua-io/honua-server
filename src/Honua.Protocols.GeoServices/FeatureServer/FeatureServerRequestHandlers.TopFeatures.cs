// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Protocols.GeoServices;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private static async Task<IResult> HandleQueryTopFeaturesGet(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.QueryTopFeatures, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var values = ToCaseInsensitiveDictionary(context.Request.Query);
        return await HandleQueryTopFeaturesCore(serviceId, layerId, values, context);
    }

    private static async Task<IResult> HandleQueryTopFeaturesPost(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var (values, readError) = await TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (values == null)
        {
            if (TryGetUnsupportedMediaType(readError, out var receivedContentType))
            {
                return CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
            }

            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid request body",
                [readError ?? "Invalid request body."]);
        }

        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!TryValidateAllowedParameters(values, queryValidator, AllowedQueryParameters.QueryTopFeatures, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        return await HandleQueryTopFeaturesCore(serviceId, layerId, values, context);
    }

    private static async Task<IResult> HandleQueryTopFeaturesCore(
        string serviceId,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.queryTopFeatures");
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.FeatureServer);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "queryTopFeatures");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);

        var requestedFormat = GetValueString(values, "f");
        if (!TryValidateOutputFormat(requestedFormat, TopFeaturesFormats, out var topFeaturesFormat, out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [formatError ?? "Output format is not supported."]);
        }

        var topFeaturesIsPbf = string.Equals(topFeaturesFormat, "pbf", StringComparison.OrdinalIgnoreCase);
        var topFeaturesIsPretty = string.Equals(requestedFormat?.Trim(), "pjson", StringComparison.OrdinalIgnoreCase);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var validationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerV2Async(
            resourceValidator,
            serviceId,
            layerId,
            context,
            logger: null,
            cancellationToken: cancellationToken);
        if (!validationResult.IsValid)
        {
            return validationResult.ErrorResult!;
        }

        var service = validationResult.Service!;
        var publication = validationResult.Publication!;
        var resource = validationResult.Resource!;
        var accessError = AccessPolicyHelpers.RequireResourceAccess(context, resource, service);
        if (accessError != null)
        {
            return accessError;
        }

        var snapshotProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await snapshotProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var storageLayerId = ResolveFeatureServerStorageLayerIdV2(snapshot, publication, resource);
        if (storageLayerId is null)
        {
            return StandardErrorHelpers.CreateNotFound(context,
                $"Layer '{resource.Metadata.Name ?? layerId.ToString(System.Globalization.CultureInfo.InvariantCulture)}' is not bound to a storage layer.");
        }

        // Parse topFilter JSON parameter
        var topFilterJson = GetValueString(values, "topFilter");
        if (string.IsNullOrWhiteSpace(topFilterJson))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "topFilter parameter is required");
        }

        if (!TryParseTopFilter(topFilterJson, out var topFilter, out var parseError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid topFilter parameter",
                [parseError ?? "topFilter must be valid JSON."]);
        }

        if (!TryParseBoolValue(values, "returnCountOnly", false, out var returnCountOnly, out var countError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid returnCountOnly parameter",
                [countError ?? "returnCountOnly must be a boolean."]);
        }

        // returnIdsOnly mirrors the normal layer query: return only the objectId-set
        // form ({ objectIdFieldName, objectIds }). The ArcGIS API for Python's
        // query_top_features() always sends it, so it must produce the id payload
        // rather than 400 (#1906).
        if (!TryParseBoolValue(values, "returnIdsOnly", false, out var returnIdsOnly, out var idsError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid returnIdsOnly parameter",
                [idsError ?? "returnIdsOnly must be a boolean."]);
        }

        // returnGeometry defaults to true (Esri default); an explicit false omits the
        // geometry from each feature, matching the normal query operation (#1906).
        if (!TryParseBoolValue(values, "returnGeometry", true, out var returnGeometry, out var geometryError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid returnGeometry parameter",
                [geometryError ?? "returnGeometry must be a boolean."]);
        }

        if (!TryParseIntValue(values, "resultOffset", out var resultOffset, out var offsetError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid resultOffset parameter",
                [offsetError ?? "resultOffset must be an integer."]);
        }

        if (resultOffset is < 0)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid resultOffset parameter",
                ["resultOffset cannot be negative."]);
        }

        if (!TryParseIntValue(values, "resultRecordCount", out var resultRecordCount, out var recordCountError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid resultRecordCount parameter",
                [recordCountError ?? "resultRecordCount must be an integer."]);
        }

        if (resultRecordCount is < 0)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid resultRecordCount parameter",
                ["resultRecordCount cannot be negative."]);
        }

        // outSR reprojects output geometry to the requested WKID (bare WKID or a
        // spatialReference JSON object). Honored so Esri clients that request a
        // display SRID receive correctly-projected geometry (#1906).
        if (!TryParseTopFeaturesOutputSrid(GetValueString(values, "outSR"), out var outputSrid, out var outSrError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid outSR parameter",
                [outSrError ?? "outSR must be a WKID or spatialReference object."]);
        }

        // Build query from common parameters
        var query = new FeatureQuery
        {
            Where = GetValueString(values, "where"),
            TopFilter = topFilter,
            SpatialReferenceSrid = resource.ReadSrid(),
            OutputSrid = outputSrid,
            Offset = resultOffset,
            Limit = resultRecordCount
        };

        var outFieldsStr = GetValueString(values, "outFields");
        if (!string.IsNullOrWhiteSpace(outFieldsStr) && outFieldsStr != "*")
        {
            query = query with
            {
                OutFields = [.. outFieldsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            };
        }

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var result = await featureReader.QueryTopFeaturesAsync(storageLayerId.Value, query, cancellationToken);

        // returnIdsOnly: return the objectId-set payload, mirroring the normal query
        // operation. pbf returns the ObjectIdsResult arm; JSON returns
        // { objectIdFieldName, objectIds } (#1906).
        if (returnIdsOnly)
        {
            var idsObjectIdField = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource);
            var ids = result.Items
                .Select(feature => GeoServicesObjectIdFieldResolver.ResolveObjectIdValue(feature, idsObjectIdField))
                .ToArray();

            if (topFeaturesIsPbf)
            {
                var (idsPayload, idsContentType) = PbfQueryFormatter.FormatIdsAsPbf(idsObjectIdField, ids);
                return Results.Bytes(idsPayload, idsContentType);
            }

            return CreateTopFeaturesJsonResult(
                new QueryResponse
                {
                    ObjectIdFieldName = idsObjectIdField,
                    ObjectIds = ids,
                    Features = null
                },
                topFeaturesIsPretty);
        }

        // returnCountOnly: Esri's queryTopFeatures honors this and returns the
        // top-feature count, not a FeatureSet. The ArcGIS API for Python issues
        // it as the first step of query_top_features paging; ignoring it left the
        // SDK with a null total and a TypeError in its paginator.
        if (returnCountOnly)
        {
            if (topFeaturesIsPbf)
            {
                var (countPayload, countContentType) = PbfQueryFormatter.FormatCountAsPbf(result.Items.Length);
                return Results.Bytes(countPayload, countContentType);
            }

            return CreateTopFeaturesJsonResult(
                new QueryResponse { Count = result.Items.Length, Features = null },
                topFeaturesIsPretty);
        }

        // f=pbf returns the FeatureResult arm of the FeatureCollectionPBuffer; ArcGIS
        // queryTopFeatures supports pbf, so emit protobuf rather than rejecting it (#1824).
        if (topFeaturesIsPbf)
        {
            var pbfFormatter = context.RequestServices.GetRequiredService<PbfQueryFormatter>();
            var topSrid = outputSrid ?? resource.ReadSrid();
            var (pbfPayload, pbfContentType) = pbfFormatter.FormatAsPbf(
                result,
                resource,
                returnGeometry: returnGeometry,
                outputSrid: topSrid,
                returnZ: false,
                returnM: false,
                geometryPrecision: null,
                maxAllowableOffset: null,
                outFields: query.OutFields.HasValue && !query.OutFields.Value.IsDefaultOrEmpty
                    ? [.. query.OutFields.Value]
                    : null);
            return Results.Bytes(pbfPayload, pbfContentType);
        }

        var responseFeatures = result.Items.Select(feature => new GeoServicesFeature
        {
            Attributes = feature.Attributes
                .Where(kvp => !FeatureAttributeVisibility.IsInternalAttribute(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            Geometry = returnGeometry
                ? GeoServicesGeometryConverter.ConvertWkbToGeoServicesGeometry(
                    feature.Geometry, null, null, false, false)
                : null,
            // returnGeometry=false must omit the geometry property entirely (not emit
            // null), matching the normal query operation (#1906).
            IncludeGeometry = returnGeometry
        }).ToArray();

        var geometryType = resource.Spatial?.GeometryType ?? MetadataV2GeometryType.None;
        var hasGeometry = returnGeometry && geometryType is not MetadataV2GeometryType.None;
        var srid = outputSrid ?? resource.ReadSrid() ?? SpatialReference.WGS84.Wkid;
        var objectIdField = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource);
        var response = new QueryResponse
        {
            GeometryType = hasGeometry ? MapGeometryTypeV2(geometryType) : null,
            SpatialReference = hasGeometry
                ? new GeoServicesSpatialReference { Wkid = srid, LatestWkid = srid }
                : null,
            ObjectIdFieldName = objectIdField,
            Fields = [.. ResolveVisibleFieldsV2(resource).Select(field => MapFieldInfoV2(field, objectIdField))],
            Features = responseFeatures,
            ExceededTransferLimit = result.HasMoreResults
        };

        return CreateTopFeaturesJsonResult(response, topFeaturesIsPretty);
    }

    /// <summary>
    /// Serializes a queryTopFeatures JSON response, emitting indented JSON for
    /// <c>f=pjson</c> and compact JSON otherwise (#1824).
    /// </summary>
    private static IResult CreateTopFeaturesJsonResult(QueryResponse response, bool pretty)
    {
        if (!pretty)
        {
            return Results.Json(response, FeatureServerJsonContext.Default.QueryResponse, contentType: "application/json");
        }

        var prettyPayload = JsonReindenter.ToIndentedUtf8Bytes(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(response, FeatureServerJsonContext.Default.QueryResponse));
        return Results.Bytes(prettyPayload, "application/json");
    }

    /// <summary>
    /// Parses the queryTopFeatures <c>outSR</c> parameter into an output WKID. Accepts
    /// a bare integer WKID or a spatialReference JSON object (<c>{ "wkid": N }</c> /
    /// <c>{ "latestWkid": N }</c>), mirroring the layer query operation (#1906). A null
    /// or empty value yields no output SRID (geometry stays in the layer's CRS).
    /// </summary>
    private static bool TryParseTopFeaturesOutputSrid(string? raw, out int? outputSrid, out string? error)
    {
        outputSrid = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var trimmed = raw.Trim();
        if (int.TryParse(trimmed, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var wkid))
        {
            if (wkid <= 0)
            {
                error = "outSR must be a positive WKID.";
                return false;
            }

            outputSrid = wkid;
            return true;
        }

        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    ((root.TryGetProperty("wkid", out var wkidElement) && wkidElement.TryGetInt32(out var jsonWkid)) ||
                     (root.TryGetProperty("latestWkid", out wkidElement) && wkidElement.TryGetInt32(out jsonWkid))) &&
                    jsonWkid > 0)
                {
                    outputSrid = jsonWkid;
                    return true;
                }
            }
            catch (JsonException)
            {
                // Fall through to the error below.
            }
        }

        error = "outSR must be a positive WKID or a spatialReference object.";
        return false;
    }

    private static bool TryParseTopFilter(string json, out TopFilter topFilter, out string? error)
    {
        topFilter = default;
        error = null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "topFilter must be a JSON object.";
                return false;
            }

            // groupByFields (required)
            if (!root.TryGetProperty("groupByFields", out var groupByElement) ||
                groupByElement.ValueKind == JsonValueKind.Null)
            {
                error = "topFilter.groupByFields is required.";
                return false;
            }

            var groupByFieldsStr = groupByElement.GetString();
            if (string.IsNullOrWhiteSpace(groupByFieldsStr))
            {
                error = "topFilter.groupByFields must not be empty.";
                return false;
            }

            var groupByFields = groupByFieldsStr
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToImmutableArray();

            // topCount (required)
            if (!root.TryGetProperty("topCount", out var topCountElement) ||
                !topCountElement.TryGetInt32(out var topCount))
            {
                error = "topFilter.topCount is required and must be a positive integer.";
                return false;
            }

            if (topCount <= 0)
            {
                error = "topFilter.topCount must be a positive integer.";
                return false;
            }

            // orderByFields (required)
            if (!root.TryGetProperty("orderByFields", out var orderByElement) ||
                orderByElement.ValueKind == JsonValueKind.Null)
            {
                error = "topFilter.orderByFields is required.";
                return false;
            }

            var orderByFieldsStr = orderByElement.GetString();
            if (string.IsNullOrWhiteSpace(orderByFieldsStr))
            {
                error = "topFilter.orderByFields must not be empty.";
                return false;
            }

            var orderByFields = ParseOrderByFields(orderByFieldsStr);

            topFilter = new TopFilter
            {
                GroupByFields = groupByFields,
                TopCount = topCount,
                OrderByFields = orderByFields
            };
            return true;
        }
        catch (JsonException)
        {
            error = "topFilter is not valid JSON.";
            return false;
        }
    }

    private static ImmutableArray<OrderByClause> ParseOrderByFields(string orderByFieldsStr)
    {
        var parts = orderByFieldsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var builder = ImmutableArray.CreateBuilder<OrderByClause>(parts.Length);

        foreach (var part in parts)
        {
            var tokens = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var fieldName = tokens[0];
            var ascending = tokens.Length < 2 ||
                            !string.Equals(tokens[1], "desc", StringComparison.OrdinalIgnoreCase);
            builder.Add(new OrderByClause(fieldName, ascending));
        }

        return builder.ToImmutable();
    }

    private static int? ResolveFeatureServerStorageLayerIdV2(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Publication publication,
        MetadataV2Resource resource)
        => snapshot.ResolveStorageLayerId(publication)
           ?? snapshot.ResolveStorageLayerId(resource)
           ?? publication.LayerIndex;
}
