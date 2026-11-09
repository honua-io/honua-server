// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Shared.Models;

public static class ModelConversions
{
    public static GeoJsonFeatureBase ToGeoJsonBase(this Feature feature)
        => GeoJsonFeatureBase.Create(
            feature.Id,
            feature.Attributes,
            feature.Geometry != null);

    public static int ToSrid(this SpatialReference spatialRef)
        => spatialRef.LatestWkid ?? spatialRef.Wkid;

    public static SpatialReference ToSpatialReference(this int srid)
        => SpatialReference.Create(srid);

    public static SpatialReference GetSpatialReference(this FeatureExtent extent)
        => SpatialReference.Create(extent.SpatialReference);

    public static int ToNumericErrorCode(this ServiceError error)
        => error.GetNumericCode() ?? 500;

    public static ServiceError ToServiceError(this Exception exception)
        => ServiceError.Create("500", GetSafeExceptionMessage(exception));

    public static ServiceError ToServiceError(this Exception exception, string code)
        => ServiceError.Create(code, GetSafeExceptionMessage(exception));

    public static ServiceError CreateValidationError(string message, string target)
        => ServiceError.Create("400", message, target);

    public static ServiceError CreateNotFoundError(string resource)
        => ServiceError.Create("404", ErrorMessages.NotFound.FormatResource(resource));

    public static ServiceError CreateUnauthorizedError(string operation)
        => ServiceError.Create("401", ErrorMessages.Security.FormatUnauthorized(operation));

    public static ServiceError CreateForbiddenError(string operation)
        => ServiceError.Create("403", ErrorMessages.Security.FormatForbidden(operation));

    private static string GetSafeExceptionMessage(Exception exception)
    {
        return exception switch
        {
            ValidationException validationException => validationException.Message,
            ResourceNotFoundException => ErrorMessages.NotFound.Resource,
            ResourceConflictException => ErrorMessages.System.ConflictState,
            ServiceUnavailableException => ErrorMessages.System.ServiceUnavailable,
            ArgumentNullException => ErrorMessages.Validation.RequiredParameterMissing,
            ArgumentException => ErrorMessages.Validation.InvalidRequestParameters,
            InvalidOperationException => ErrorMessages.System.InvalidOperation,
            UnauthorizedAccessException => ErrorMessages.Security.Unauthorized,
            NotSupportedException => ErrorMessages.System.NotSupported,
            OperationCanceledException => ErrorMessages.System.RequestCancelled,
            TimeoutException => ErrorMessages.System.RequestTimeout,
            _ => ErrorMessages.System.UnexpectedError
        };
    }
}
