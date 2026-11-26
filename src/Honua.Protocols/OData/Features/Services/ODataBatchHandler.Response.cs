// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.OData.Models;

namespace Honua.Protocols.OData.Services;

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
        var hiddenFields = layer.Resource.HiddenSchemaFieldNames();
        var payload = ODataUtilityService.BuildFeaturePayload(new ODataUtilityService.ODataFeaturePayloadRequest
        {
            LayerId = layer.PublicLayerId,
            Feature = feature,
            Geometry = geometry,
            Attributes = attributes,
            HiddenFields = hiddenFields
        });
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
        var json = ODataUtilityService.SerializeForEtag(payload);
        return _etagService.ComputeETag(json);
    }
}
