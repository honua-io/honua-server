// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Conversion methods between shared models and protocol-specific models
/// </summary>
public static class ModelConversions
{
    /// <summary>
    /// Converts the core Feature to a GeoJsonFeatureBase
    /// </summary>
    /// <param name="feature">Core feature</param>
    /// <returns>GeoJSON feature base</returns>
    public static GeoJsonFeatureBase ToGeoJsonBase(this Feature feature)
        => GeoJsonFeatureBase.Create(
            feature.Id,
            feature.Attributes,
            feature.Geometry != null);

    /// <summary>
    /// Converts a SpatialReference to the simple SRID integer
    /// </summary>
    /// <param name="spatialRef">Spatial reference</param>
    /// <returns>SRID integer</returns>
    public static int ToSrid(this SpatialReference spatialRef)
        => spatialRef.LatestWkid ?? spatialRef.Wkid;

    /// <summary>
    /// Converts an SRID to a SpatialReference
    /// </summary>
    /// <param name="srid">Spatial Reference System ID</param>
    /// <returns>Spatial reference</returns>
    public static SpatialReference ToSpatialReference(this int srid)
        => SpatialReference.Create(srid);

    /// <summary>
    /// Converts a FeatureExtent to include SpatialReference information
    /// </summary>
    /// <param name="extent">Feature extent</param>
    /// <returns>Spatial reference from the extent</returns>
    public static SpatialReference GetSpatialReference(this FeatureExtent extent)
        => SpatialReference.Create(extent.SpatialReference);

    /// <summary>
    /// Converts ServiceError to numeric code format (for GeoServices compatibility)
    /// </summary>
    /// <param name="error">Service error</param>
    /// <returns>Numeric error code or 500 as default</returns>
    public static int ToNumericErrorCode(this ServiceError error)
        => error.GetNumericCode() ?? 500;

    /// <summary>
    /// Converts an exception to a ServiceError
    /// </summary>
    /// <param name="exception">Exception to convert</param>
    /// <returns>Service error</returns>
    public static ServiceError ToServiceError(this Exception exception)
        => ServiceError.Create("500", GetSafeExceptionMessage(exception));

    /// <summary>
    /// Converts an exception to a ServiceError with custom code
    /// </summary>
    /// <param name="exception">Exception to convert</param>
    /// <param name="code">Error code to use</param>
    /// <returns>Service error</returns>
    public static ServiceError ToServiceError(this Exception exception, string code)
        => ServiceError.Create(code, GetSafeExceptionMessage(exception));

    /// <summary>
    /// Creates a validation ServiceError
    /// </summary>
    /// <param name="message">Validation message</param>
    /// <param name="target">Field or parameter that failed validation</param>
    /// <returns>Service error</returns>
    public static ServiceError CreateValidationError(string message, string target)
        => ServiceError.Create("400", message, target);

    /// <summary>
    /// Creates a not found ServiceError
    /// </summary>
    /// <param name="resource">Resource that was not found</param>
    /// <returns>Service error</returns>
    public static ServiceError CreateNotFoundError(string resource)
        => ServiceError.Create("404", $"{resource} not found");

    /// <summary>
    /// Creates an unauthorized ServiceError
    /// </summary>
    /// <param name="operation">Operation that was unauthorized</param>
    /// <returns>Service error</returns>
    public static ServiceError CreateUnauthorizedError(string operation)
        => ServiceError.Create("401", $"Unauthorized to perform {operation}");

    /// <summary>
    /// Creates a forbidden ServiceError
    /// </summary>
    /// <param name="operation">Operation that is forbidden</param>
    /// <returns>Service error</returns>
    public static ServiceError CreateForbiddenError(string operation)
        => ServiceError.Create("403", $"Forbidden to perform {operation}");

    private static string GetSafeExceptionMessage(Exception exception)
    {
        return exception switch
        {
            ValidationException validationException => validationException.Message,
            ResourceNotFoundException => "The requested resource was not found.",
            ResourceConflictException => "The request could not be completed due to a conflict with the current state.",
            ServiceUnavailableException => "The service is temporarily unavailable. Please try again later.",
            ArgumentNullException => "A required parameter was not provided.",
            ArgumentException => "Invalid request parameters.",
            InvalidOperationException => "The requested operation is not valid in the current state.",
            UnauthorizedAccessException => "Authentication is required to access this resource.",
            NotSupportedException => "The requested operation is not supported.",
            OperationCanceledException => "The request was cancelled or timed out.",
            TimeoutException => "The request timed out.",
            _ => "An unexpected error occurred."
        };
    }
}
