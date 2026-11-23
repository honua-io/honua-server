// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Authorization.Domain;
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
using Honua.Infrastructure.Services;
using Honua.Infrastructure.Validation;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;
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
        var accessError = await AccessPolicyHelpers.RequireResourceAccessAsync(
            context, resource, AuthorizationOperation.Query, service, cancellationToken).ConfigureAwait(false);
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

        if (FirstUnavailableTopFilterField(resource, topFilter) is { } unavailableField)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid topFilter parameter",
                [ErrorMessages.NotFound.FormatField(unavailableField, resource.Metadata.Name)]);
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
        // rather than 400.
        if (!TryParseBoolValue(values, "returnIdsOnly", false, out var returnIdsOnly, out var idsError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid returnIdsOnly parameter",
                [idsError ?? "returnIdsOnly must be a boolean."]);
        }

        // returnGeometry defaults to true (Esri default); an explicit false omits the
        // geometry from each feature, matching the normal query operation.
        if (!TryParseBoolValue(values, "returnGeometry", true, out var returnGeometry, out var geometryError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid returnGeometry parameter",
                [geometryError ?? "returnGeometry must be a boolean."]);
        }

        if (!TryParseBoolValue(values, "returnZ", false, out var returnZ, out var returnZError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid returnZ parameter",
                [returnZError ?? "returnZ must be a boolean."]);
        }

        if (!TryParseBoolValue(values, "returnM", false, out var returnM, out var returnMError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid returnM parameter",
                [returnMError ?? "returnM must be a boolean."]);
        }

        if (!TryParseIntValue(values, "geometryPrecision", out var geometryPrecision, out var precisionError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid geometryPrecision parameter",
                [precisionError ?? "geometryPrecision must be an integer."]);
        }

        if (geometryPrecision is < 0)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid geometryPrecision parameter",
                ["geometryPrecision cannot be negative."]);
        }

        double? maxAllowableOffset = null;
        var maxAllowableOffsetStr = GetValueString(values, "maxAllowableOffset");
        if (!string.IsNullOrWhiteSpace(maxAllowableOffsetStr))
        {
            if (!double.TryParse(maxAllowableOffsetStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedOffset) ||
                double.IsNaN(parsedOffset) || double.IsInfinity(parsedOffset) || parsedOffset < 0)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid maxAllowableOffset parameter",
                    ["maxAllowableOffset must be a non-negative number."]);
            }

            maxAllowableOffset = parsedOffset;
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

        // Same bound and wording as the query operation's pagination validation.
        var queryLimits = ResolveQueryLimits(context);
        if (resultOffset > queryLimits.MaxOffset)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid resultOffset parameter",
                [$"resultOffset cannot exceed {queryLimits.MaxOffset}. Maximum offset: {queryLimits.MaxOffset}."]);
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
        // display SRID receive correctly-projected geometry.
        if (!TryParseTopFeaturesOutputSrid(GetValueString(values, "outSR"), out var outputSrid, out var outSrError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid outSR parameter",
                [outSrError ?? "outSR must be a WKID or spatialReference object."]);
        }

        // Translate where through the shared filter pipeline, then overlay the
        // top-features parameters. A raw Where string skips SqlFilter translation.
        if (!TryBuildAnalyticsFeatureQuery(context, resource, GetValueString(values, "where"), out var query, out var whereError))
        {
            return whereError!;
        }

        // Feature responses page at the service record cap and read one probe row for
        // exceededTransferLimit; ids responses carry no page cap, as on query. A count-only
        // response ignores resultOffset and resultRecordCount so it is the paging total.
        var featurePage = !returnIdsOnly && !returnCountOnly;
        var countOnlyResponse = returnCountOnly && !returnIdsOnly;
        int? pageLimit = resultRecordCount;
        if (featurePage)
        {
            pageLimit = ResolveTopFeaturesPageLimit(resultRecordCount, queryLimits.MaxRecordCount);
        }
        else if (countOnlyResponse)
        {
            pageLimit = null;
        }

        query = query with
        {
            TopFilter = topFilter,
            SpatialReferenceSrid = resource.ReadSrid(),
            OutputSrid = outputSrid,
            IncludeZ = returnZ,
            IncludeM = returnM,
            Offset = countOnlyResponse ? null : resultOffset,
            Limit = featurePage && pageLimit < int.MaxValue ? pageLimit + 1 : pageLimit
        };

        var outFieldsStr = GetValueString(values, "outFields");
        if (!string.IsNullOrWhiteSpace(outFieldsStr) && outFieldsStr != "*")
        {
            query = query with
            {
                OutFields = [.. outFieldsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            };
        }

        var (filteredQuery, filterError) = await ApplyAnalyticsFiltersAsync(
            context, resource, values, query, cancellationToken).ConfigureAwait(false);
        if (filterError != null)
        {
            return filterError;
        }

        query = filteredQuery;

        QueryResult<Feature> result;
        try
        {
            var featureReader = await ResolveAnalyticsFeatureReaderAsync(
                context, snapshot, service, resource, publication, storageLayerId.Value, cancellationToken).ConfigureAwait(false);
            result = TrimTopFeaturesPage(
                await featureReader.QueryTopFeaturesAsync(storageLayerId.Value, query, cancellationToken),
                pageLimit);
        }
        catch (NotSupportedException)
        {
            return StandardErrorHelpers.CreateNotImplemented(
                context,
                "queryTopFeatures is not supported by the configured feature provider for this layer.");
        }

        var schemaResource = await HideMaskedFieldsAsync(context, resource, cancellationToken).ConfigureAwait(false);

        // returnIdsOnly: return the objectId-set payload, mirroring the normal query
        // operation. pbf returns the ObjectIdsResult arm; JSON returns
        // { objectIdFieldName, objectIds }.
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

        var requestedOutFields = query.OutFields.HasValue && !query.OutFields.Value.IsDefaultOrEmpty
            ? [.. query.OutFields.Value]
            : (string[]?)null;

        // f=pbf returns the FeatureResult arm of the FeatureCollectionPBuffer; ArcGIS
        // queryTopFeatures supports pbf, so emit protobuf rather than rejecting it.
        if (topFeaturesIsPbf)
        {
            var pbfFormatter = context.RequestServices.GetRequiredService<PbfQueryFormatter>();
            var topSrid = outputSrid ?? resource.ReadSrid();
            var (pbfPayload, _) = pbfFormatter.FormatAsPbfStream(result, schemaResource, new PbfStreamEncodingOptions
            {
                ReturnGeometry = returnGeometry,
                OutputSrid = topSrid,
                ReturnZ = returnZ,
                ReturnM = returnM,
                GeometryPrecision = geometryPrecision,
                MaxAllowableOffset = maxAllowableOffset,
                OutFields = requestedOutFields
            });
            return pbfPayload;
        }

        var baseGeometryLimits = context.RequestServices.GetService<IOptions<LimitsOptions>>()?.Value?.Geometry ?? new GeometryLimits();
        var effectiveGeometryLimits = (geometryPrecision.HasValue || maxAllowableOffset.HasValue)
            ? GeometryOutputProcessor.CreateEffectiveLimits(
                baseGeometryLimits,
                geometryPrecision,
                maxAllowableOffset,
                forceSimplify: maxAllowableOffset is > 0)
            : null;

        return CreateTopFeaturesJsonResult(
            BuildTopFeaturesJsonResponse(
                result,
                schemaResource,
                returnGeometry,
                outputSrid,
                effectiveGeometryLimits,
                returnZ,
                returnM,
                requestedOutFields),
            topFeaturesIsPretty);
    }

    internal static int ResolveTopFeaturesPageLimit(int? resultRecordCount, int maxRecordCount)
    {
        var cap = Math.Max(1, maxRecordCount);
        return resultRecordCount is > 0 ? Math.Min(resultRecordCount.Value, cap) : cap;
    }

    internal static QueryResult<Feature> TrimTopFeaturesPage(QueryResult<Feature> result, int? pageLimit)
        => pageLimit is int limit && result.Items.Length > limit
            ? QueryResult<Feature>.Create(limit, result.Items.RemoveRange(limit, result.Items.Length - limit), hasMoreResults: true)
            : result;

    private static QueryLimits ResolveQueryLimits(HttpContext context)
        => context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.Query;

    internal static QueryResponse BuildTopFeaturesJsonResponse(
        QueryResult<Feature> result,
        MetadataV2Object resource,
        bool returnGeometry,
        int? outputSrid,
        GeometryLimits? geometryLimits = null,
        bool returnZ = false,
        bool returnM = false,
        IReadOnlyList<string>? outFields = null)
    {
        var geometryType = resource.Spatial?.GeometryType ?? MetadataV2GeometryType.None;
        var hasGeometry = returnGeometry && geometryType is not MetadataV2GeometryType.None;
        var srid = outputSrid ?? resource.ReadSrid() ?? SpatialReference.WGS84.Wkid;
        var objectIdField = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource);
        HashSet<string>? requestedFieldSet = outFields is { Count: > 0 } && !outFields.Any(static f => f == "*")
            ? new HashSet<string>(outFields, StringComparer.OrdinalIgnoreCase)
            : null;
        var temporalFieldTypes = GeoServicesFieldConventions.ResolveTemporalFieldTypes(resource);
        var responseFeatures = result.Items.Select(feature =>
        {
            var attributes = feature.Attributes
                .Where(kvp => !FeatureAttributeVisibility.IsInternalAttribute(kvp.Key)
                    && (requestedFieldSet is null || requestedFieldSet.Contains(kvp.Key)))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            GeoServicesFieldConventions.CoerceTemporalAttributes(attributes, temporalFieldTypes);
            return new GeoServicesFeature
            {
                Attributes = attributes,
                Geometry = returnGeometry
                    ? GeoServicesGeometryConverter.ConvertWkbToGeoServicesGeometry(
                        feature.Geometry, srid, geometryLimits, returnZ, returnM)
                    : null,
                // returnGeometry=false must omit the geometry property entirely (not emit
                // null), matching the normal query operation.
                IncludeGeometry = returnGeometry
            };
        }).ToArray();

        var visibleFields = ResolveVisibleFieldsV2(resource);
        if (requestedFieldSet is not null)
        {
            visibleFields = visibleFields.Where(field => requestedFieldSet.Contains(field.Name));
        }

        var response = new QueryResponse
        {
            GeometryType = hasGeometry ? MapGeometryTypeV2(geometryType) : null,
            SpatialReference = hasGeometry
                ? new GeoServicesSpatialReference { Wkid = srid, LatestWkid = srid }
                : null,
            ObjectIdFieldName = objectIdField,
            Fields = [.. visibleFields.Select(field => MapFieldInfoV2(field, objectIdField))],
            Features = responseFeatures,
            ExceededTransferLimit = result.HasMoreResults
        };

        return response;
    }

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
                    (FeatureServerJsonMembers.ReadInt32(root, "wkid") ?? FeatureServerJsonMembers.ReadInt32(root, "latestWkid")) is { } jsonWkid &&
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

            var groupByFieldsStr = FeatureServerJsonMembers.ReadString(root, "groupByFields");
            if (string.IsNullOrWhiteSpace(groupByFieldsStr))
            {
                error = "topFilter.groupByFields is required and must be a non-empty comma-separated string.";
                return false;
            }

            var groupByFields = groupByFieldsStr
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToImmutableArray();

            // topCount (required)
            if (FeatureServerJsonMembers.ReadInt32(root, "topCount") is not { } topCount)
            {
                error = "topFilter.topCount is required and must be a positive integer.";
                return false;
            }

            if (topCount <= 0)
            {
                error = "topFilter.topCount must be a positive integer.";
                return false;
            }

            var orderByFieldsStr = FeatureServerJsonMembers.ReadString(root, "orderByFields");
            if (string.IsNullOrWhiteSpace(orderByFieldsStr))
            {
                error = "topFilter.orderByFields is required and must be a non-empty comma-separated string.";
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

    // topFilter groups and orders only by fields the layer publishes and does not hide; a hidden
    // field is answered like an undeclared one.
    private static string? FirstUnavailableTopFilterField(MetadataV2Object resource, TopFilter topFilter)
        => topFilter.GroupByFields
            .Concat(topFilter.OrderByFields.Select(clause => clause.Field))
            .FirstOrDefault(field => resource.FindVisibleSchemaField(field) is null &&
                (!CallerOrderByFields.AllowedCoreOrderByFields.Contains(field) || resource.IsHiddenSchemaField(field)));

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
        MetadataV2Layer publication,
        MetadataV2Object resource)
        => snapshot.ResolveStorageLayerId(publication)
           ?? snapshot.ResolveStorageLayerId(resource)
           ?? publication.LayerIndex;
}
