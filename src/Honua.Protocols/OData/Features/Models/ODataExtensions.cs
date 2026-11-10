// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.OData.Models;

public static class ODataExtensions
{
    public static ErrorDetail ToErrorDetail(this ServiceError serviceError)
        => new()
        {
            Code = serviceError.Code,
            Message = serviceError.Message,
            Target = serviceError.Target
        };

    public static ServiceError ToServiceError(this ErrorDetail errorDetail)
        => ServiceError.Create(errorDetail.Code, errorDetail.Message, errorDetail.Target, null);

    public static ErrorDetails ToErrorDetails(this ServiceError serviceError)
        => new()
        {
            Code = serviceError.Code,
            Message = serviceError.Message,
            Details = serviceError.Details?.Select(d => new ErrorDetail
            {
                Code = serviceError.Code,
                Message = d,
                Target = serviceError.Target
            }).ToArray()
        };

    public static ServiceError ToServiceError(this ErrorDetails errorDetails)
        => ServiceError.Create(
            errorDetails.Code,
            errorDetails.Message,
            null,
            errorDetails.Details?.Select(d => d.Message).ToArray());

    public static ODataFeatureResponse ToODataFeatureResponse(this GeoJsonFeatureBase featureBase,
        string context,
        int layerId,
        ODataSpatialGeometry? geometry = null)
    {
        // Convert ID to ObjectId (long)
        long objectId = featureBase.Id switch
        {
            long longId => longId,
            int intId => intId,
            string strId when long.TryParse(strId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId) => parsedId,
            _ => 0
        };

        return new()
        {
            Context = context,
            LayerId = layerId,
            ObjectId = objectId,
            Attributes = ODataAttributeSerializer.Serialize(featureBase.Properties),
            Geometry = geometry
        };
    }

    public static GeoJsonFeatureBase ToGeoJsonFeatureBase(this ODataFeatureResponse featureResponse)
        => GeoJsonFeatureBase.Create(
            featureResponse.ObjectId,
            ODataAttributeSerializer.Deserialize(featureResponse.Attributes),
            featureResponse.Geometry != null);

    public static (string Context, long? Count, string? NextLink) ToODataResponseProperties(this PagedResponseBase pagedBase,
        string context,
        string? nextLink = null)
        => (context, pagedBase.TotalCount, nextLink);

    public static PagedResponseBase ToPagedResponseBase(this ODataResponse oDataResponse)
        => PagedResponseBase.Create(
            oDataResponse.Value?.Length ?? 0,
            oDataResponse.Count);

    public static ODataError ToODataError(this ServiceError serviceError)
        => new()
        {
            Error = serviceError.ToErrorDetails()
        };

    public static ServiceError ToServiceError(this ODataError oDataError)
        => oDataError.Error.ToServiceError();

    public static ODataError CreateValidationError(string message, string target)
        => ModelConversions.CreateValidationError(message, target).ToODataError();

    public static ODataError CreateNotFoundError(string resource)
        => ModelConversions.CreateNotFoundError(resource).ToODataError();
}
