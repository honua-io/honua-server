// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Protocols.OData.Models;

namespace Honua.Server.Features.Protocols.OData.Services;

/// <summary>
/// Response assembly and error formatting for the OData $batch handler.
/// Builds OData feature payloads, computes ETags, and wraps batch sub-request
/// results into the success / error envelope shape consumed by the batch writer.
/// </summary>
internal sealed partial class ODataBatchHandler
{
    private Dictionary<string, object?> FeatureToBody(
        Feature feature,
        ODataBatchLayerContext layer,
        AxisOrder axisOrder,
        string baseUrl,
        out string etag)
    {
        var geometry = ODataGeometryConverter.ConvertWkbToGeometry(
            _geometryService,
            feature.Geometry,
            layer.Srid,
            axisOrder);
        var attributes = ODataAttributeSerializer.Serialize(feature.Attributes);
        var payload = ODataUtilityService.BuildFeaturePayload(layer.PublicLayerId, feature, geometry, attributes);
        etag = ComputeFeatureEtag(payload);
        payload["@odata.etag"] = etag;
        payload["@odata.context"] = ODataUtilityService.BuildContextUrl(baseUrl, "Features", isSingle: true);
        return payload;
    }

    private static ODataBatchResponseItem CreateSuccessResponse(
        string id,
        int status,
        object? body,
        Dictionary<string, string>? headers = null,
        Feature? mutationFeature = null)
    {
        return new ODataBatchResponseItem
        {
            Id = id,
            Status = status,
            Headers = headers,
            Body = body,
            MutationFeature = mutationFeature
        };
    }

    private static ODataBatchResponseItem CreateErrorResponse(string id, int status, string code, string message)
    {
        return new ODataBatchResponseItem
        {
            Id = id,
            Status = status,
            Body = new ODataError
            {
                Error = new ErrorDetails
                {
                    Code = code,
                    Message = message
                }
            }
        };
    }

    private string ComputeFeatureEtag(Dictionary<string, object?> payload)
    {
        var canonical = ODataUtilityService.NormalizeForEtag(payload);
        var json = JsonSerializer.SerializeToUtf8Bytes(canonical);
        return _etagService.ComputeETag(json);
    }
}
