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
using Honua.Server.Features.Protocols.GeoServices;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Services;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private static async Task<IResult> HandleQueryTopFeaturesGet(
        string serviceId,
        int layerId,
        HttpContext context)
    {
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

        // Build query from common parameters
        var query = new FeatureQuery
        {
            Where = GetValueString(values, "where"),
            TopFilter = topFilter,
            SpatialReferenceSrid = resource.ReadSrid()
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

        var responseFeatures = result.Items.Select(feature => new GeoServicesFeature
        {
            Attributes = feature.Attributes
                .Where(kvp => !FeatureAttributeVisibility.IsInternalAttribute(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            Geometry = GeoServicesGeometryConverter.ConvertWkbToGeoServicesGeometry(
                feature.Geometry, null, null, false, false)
        }).ToArray();

        var geometryType = resource.Spatial?.GeometryType ?? MetadataV2GeometryType.None;
        var hasGeometry = geometryType is not MetadataV2GeometryType.None;
        var srid = resource.ReadSrid() ?? SpatialReference.WGS84.Wkid;
        var response = new QueryResponse
        {
            GeometryType = hasGeometry ? MapGeometryTypeV2(geometryType) : null,
            SpatialReference = hasGeometry
                ? new GeoServicesSpatialReference { Wkid = srid, LatestWkid = srid }
                : null,
            Fields = [.. ResolveVisibleFieldsV2(resource).Select(MapFieldInfoV2)],
            Features = responseFeatures,
            ExceededTransferLimit = result.HasMoreResults
        };

        return Results.Json(response, FeatureServerJsonContext.Default.QueryResponse, contentType: "application/json");
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
