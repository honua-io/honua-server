// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Models;

/// <summary>
/// Standard GeoServices error codes used in GeoServices REST API responses
/// Based on GeoServices REST error response documentation
/// </summary>
internal static class GeoServicesErrorCodes
{
    /// <summary>
    /// Bad Request - Invalid parameters or malformed request
    /// Used for: Invalid query syntax, malformed JSON, parameter validation failures
    /// </summary>
    public const int BadRequest = 400;

    /// <summary>
    /// Unauthorized - Authentication required or failed
    /// Used for: Missing or invalid API key
    /// </summary>
    public const int Unauthorized = 401;

    /// <summary>
    /// Forbidden - Access denied to resource
    /// Used for: Valid authentication but insufficient permissions
    /// </summary>
    public const int Forbidden = 403;

    /// <summary>
    /// Not Found - Requested resource does not exist
    /// Used for: Non-existent service, layer, or feature
    /// </summary>
    public const int NotFound = 404;

    /// <summary>
    /// Method Not Allowed - HTTP method not supported for endpoint
    /// Used for: Wrong HTTP verb used for endpoint
    /// </summary>
    public const int MethodNotAllowed = 405;

    /// <summary>
    /// Request Timeout - Request took too long to process
    /// Used for: Query timeout, operation timeout
    /// </summary>
    public const int RequestTimeout = 408;

    /// <summary>
    /// Payload Too Large - Request entity is too large
    /// Used for: File uploads, large request bodies
    /// </summary>
    public const int PayloadTooLarge = 413;

    /// <summary>
    /// Unprocessable Entity - Valid syntax but cannot be processed
    /// Used for: Invalid geometry, unsupported spatial reference
    /// </summary>
    public const int UnprocessableEntity = 422;

    /// <summary>
    /// Invalid Token - GeoServices-specific error code for authentication issues
    /// Used for: Expired or invalid authentication tokens
    /// </summary>
    public const int InvalidToken = 498;

    /// <summary>
    /// Token Required - GeoServices-specific error code for missing authentication
    /// Used for: Missing authentication when required
    /// </summary>
    public const int TokenRequired = 499;

    /// <summary>
    /// Internal Server Error - Unexpected server error
    /// Used for: Database errors, unhandled exceptions
    /// </summary>
    public const int InternalServerError = 500;

    /// <summary>
    /// Bad Gateway - Upstream service error
    /// Used for: Database connection failures, external service errors
    /// </summary>
    public const int BadGateway = 502;

    /// <summary>
    /// Service Unavailable - Service temporarily unavailable
    /// Used for: Maintenance mode, overload conditions
    /// </summary>
    public const int ServiceUnavailable = 503;

    /// <summary>
    /// Gets the appropriate GeoServices error code for an HTTP status code
    /// </summary>
    /// <param name="httpStatusCode">HTTP status code</param>
    /// <returns>Corresponding GeoServices error code</returns>
    public static int FromHttpStatusCode(int httpStatusCode) => httpStatusCode switch
    {
        400 => BadRequest,
        401 => Unauthorized,
        403 => Forbidden,
        404 => NotFound,
        405 => MethodNotAllowed,
        408 => RequestTimeout,
        413 => PayloadTooLarge,
        422 => UnprocessableEntity,
        498 => InvalidToken,
        499 => TokenRequired,
        500 => InternalServerError,
        502 => BadGateway,
        503 => ServiceUnavailable,
        _ => InternalServerError // Default to 500 for unknown codes
    };
}
