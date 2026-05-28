// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Features.Protocols.OData.Models;

/// <summary>
/// Extension methods for converting between OData models and shared components
/// </summary>
public static class ODataExtensions
{
    /// <summary>
    /// Converts a shared ServiceError to OData ErrorDetail
    /// </summary>
    /// <param name="serviceError">Shared service error</param>
    /// <returns>OData error detail</returns>
    public static ErrorDetail ToErrorDetail(this ServiceError serviceError)
        => new()
        {
            Code = serviceError.Code,
            Message = serviceError.Message,
            Target = serviceError.Target
        };

    /// <summary>
    /// Converts OData ErrorDetail to shared ServiceError
    /// </summary>
    /// <param name="errorDetail">OData error detail</param>
    /// <returns>Shared service error</returns>
    public static ServiceError ToServiceError(this ErrorDetail errorDetail)
        => ServiceError.Create(errorDetail.Code, errorDetail.Message, errorDetail.Target, null);

    /// <summary>
    /// Converts a shared ServiceError to OData ErrorDetails
    /// </summary>
    /// <param name="serviceError">Shared service error</param>
    /// <returns>OData error details</returns>
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

    /// <summary>
    /// Converts OData ErrorDetails to shared ServiceError
    /// </summary>
    /// <param name="errorDetails">OData error details</param>
    /// <returns>Shared service error</returns>
    public static ServiceError ToServiceError(this ErrorDetails errorDetails)
        => ServiceError.Create(
            errorDetails.Code,
            errorDetails.Message,
            null,
            errorDetails.Details?.Select(d => d.Message).ToArray());

    /// <summary>
    /// Converts a shared GeoJsonFeatureBase to OData ODataFeatureResponse
    /// </summary>
    /// <param name="featureBase">Shared GeoJSON feature base</param>
    /// <param name="context">OData context</param>
    /// <param name="layerId">Layer ID</param>
    /// <param name="geometry">Optional geometry JSON</param>
    /// <returns>OData feature response</returns>
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

    /// <summary>
    /// Converts OData ODataFeatureResponse to shared GeoJsonFeatureBase
    /// </summary>
    /// <param name="featureResponse">OData feature response</param>
    /// <returns>Shared GeoJSON feature base</returns>
    public static GeoJsonFeatureBase ToGeoJsonFeatureBase(this ODataFeatureResponse featureResponse)
        => GeoJsonFeatureBase.Create(
            featureResponse.ObjectId,
            ODataAttributeSerializer.Deserialize(featureResponse.Attributes),
            featureResponse.Geometry != null);

    /// <summary>
    /// Converts a shared PagedResponseBase to OData ODataResponse properties
    /// </summary>
    /// <param name="pagedBase">Shared paged response base</param>
    /// <param name="context">OData context</param>
    /// <param name="nextLink">Optional next link for pagination</param>
    /// <returns>Tuple of OData response properties</returns>
    public static (string Context, long? Count, string? NextLink) ToODataResponseProperties(this PagedResponseBase pagedBase,
        string context,
        string? nextLink = null)
        => (context, pagedBase.TotalCount, nextLink);

    /// <summary>
    /// Creates a PagedResponseBase from OData ODataResponse
    /// </summary>
    /// <param name="oDataResponse">OData response</param>
    /// <returns>Shared paged response base</returns>
    public static PagedResponseBase ToPagedResponseBase(this ODataResponse oDataResponse)
        => PagedResponseBase.Create(
            oDataResponse.Value?.Length ?? 0,
            oDataResponse.Count);

    /// <summary>
    /// Creates an ODataError from a shared ServiceError
    /// </summary>
    /// <param name="serviceError">Shared service error</param>
    /// <returns>OData error</returns>
    public static ODataError ToODataError(this ServiceError serviceError)
        => new()
        {
            Error = serviceError.ToErrorDetails()
        };

    /// <summary>
    /// Converts ODataError to shared ServiceError
    /// </summary>
    /// <param name="oDataError">OData error</param>
    /// <returns>Shared service error</returns>
    public static ServiceError ToServiceError(this ODataError oDataError)
        => oDataError.Error.ToServiceError();

    /// <summary>
    /// Creates a validation error in OData format
    /// </summary>
    /// <param name="message">Validation message</param>
    /// <param name="target">Field that failed validation</param>
    /// <returns>OData error</returns>
    public static ODataError CreateValidationError(string message, string target)
        => ModelConversions.CreateValidationError(message, target).ToODataError();

    /// <summary>
    /// Creates a not found error in OData format
    /// </summary>
    /// <param name="resource">Resource that was not found</param>
    /// <returns>OData error</returns>
    public static ODataError CreateNotFoundError(string resource)
        => ModelConversions.CreateNotFoundError(resource).ToODataError();
}
