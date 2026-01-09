// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.OData.Models;

namespace Honua.Server.Features.OData.Services;

/// <summary>
/// Service for handling common OData utilities including error responses, headers, and URL generation.
/// Provides shared functionality across OData operations.
/// </summary>
internal static class ODataUtilityService
{
    /// <summary>
    /// OData protocol version
    /// </summary>
    private const string ODataVersion = "4.0";

    /// <summary>
    /// OData JSON content type with minimal metadata
    /// </summary>
    private const string ODataContentType = "application/json;odata.metadata=minimal";

    /// <summary>
    /// Sets required OData headers on the HTTP response.
    /// </summary>
    public static void SetODataHeaders(HttpContext context, string? etag = null)
    {
        context.Response.Headers["OData-Version"] = ODataVersion;
        if (etag != null)
        {
            context.Response.Headers.ETag = $"\"{etag}\"";
        }
    }

    /// <summary>
    /// Creates an OData v4 compliant error response using standardized error handling.
    /// See: https://docs.oasis-open.org/odata/odata-json-format/v4.01/odata-json-format-v4.01.html#sec_ErrorResponseBody
    /// </summary>
    public static IResult CreateODataError(
        HttpContext context,
        string code,
        string message,
        int statusCode = 400,
        string? target = null,
        ErrorDetail[]? details = null)
    {
        // Convert to standardized error response
        var additionalDetails = details?.Select(d => d.Message).ToList();
        var errorResponse = statusCode switch
        {
            StatusCodes.Status400BadRequest => StandardErrorResponse.BadRequest(message, additionalDetails),
            StatusCodes.Status401Unauthorized => StandardErrorResponse.Unauthorized(message, additionalDetails),
            StatusCodes.Status403Forbidden => StandardErrorResponse.Forbidden(message, additionalDetails),
            StatusCodes.Status404NotFound => StandardErrorResponse.NotFound(message, additionalDetails),
            StatusCodes.Status409Conflict => StandardErrorResponse.Conflict(message, additionalDetails),
            StatusCodes.Status500InternalServerError => StandardErrorResponse.InternalServerError(message, additionalDetails),
            StatusCodes.Status503ServiceUnavailable => StandardErrorResponse.ServiceUnavailable(message, null, additionalDetails),
            _ => new StandardErrorResponse(statusCode, code, message, additionalDetails)
        };

        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    /// <summary>
    /// Creates an OData error response using standardized error handling.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="detail">The error detail message.</param>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="additionalDetails">Optional additional details.</param>
    /// <returns>A standardized OData error response.</returns>
    public static IResult CreateStandardODataError(HttpContext context, string detail, int statusCode = 400, IReadOnlyList<string>? additionalDetails = null)
    {
        var errorResponse = statusCode switch
        {
            StatusCodes.Status400BadRequest => StandardErrorResponse.BadRequest(detail, additionalDetails),
            StatusCodes.Status401Unauthorized => StandardErrorResponse.Unauthorized(detail, additionalDetails),
            StatusCodes.Status403Forbidden => StandardErrorResponse.Forbidden(detail, additionalDetails),
            StatusCodes.Status404NotFound => StandardErrorResponse.NotFound(detail, additionalDetails),
            StatusCodes.Status409Conflict => StandardErrorResponse.Conflict(detail, additionalDetails),
            StatusCodes.Status500InternalServerError => StandardErrorResponse.InternalServerError(detail, additionalDetails),
            StatusCodes.Status503ServiceUnavailable => StandardErrorResponse.ServiceUnavailable(detail, null, additionalDetails),
            _ => new StandardErrorResponse(statusCode, "Error", detail, additionalDetails)
        };

        return StandardErrorResponseFormatter.FormatError(context, errorResponse);
    }

    /// <summary>
    /// Generates the @odata.nextLink URL for pagination support.
    /// </summary>
    public static string GenerateNextLink(
        HttpRequest request,
        int layerId,
        int nextSkip,
        int top,
        string? filter,
        string? select,
        string? orderby,
        bool? count)
    {
        var baseUrl = BaseUrlResolver.GetBaseUrl(request);
        var queryParams = new List<string>
        {
            $"$skip={nextSkip}",
            $"$top={top}"
        };

        if (!string.IsNullOrWhiteSpace(filter))
        {
            queryParams.Add($"$filter={Uri.EscapeDataString(filter)}");
        }

        if (!string.IsNullOrWhiteSpace(select))
        {
            queryParams.Add($"$select={Uri.EscapeDataString(select)}");
        }

        if (!string.IsNullOrWhiteSpace(orderby))
        {
            queryParams.Add($"$orderby={Uri.EscapeDataString(orderby)}");
        }

        if (count == true)
        {
            queryParams.Add("$count=true");
        }

        return $"{baseUrl}/odata/Features({layerId})?{string.Join("&", queryParams)}";
    }

    /// <summary>
    /// Gets a timeout-aware cancellation token from the HTTP context.
    /// Prefers the limits timeout token if available, otherwise uses request cancellation.
    /// </summary>
    public static CancellationToken GetTimeoutAwareCancellationToken(HttpContext context)
    {
        if (context.Items.TryGetValue("LimitsTimeoutToken", out var tokenObj) && tokenObj is CancellationToken timeoutToken)
        {
            return timeoutToken;
        }

        return context.RequestAborted;
    }

    /// <summary>
    /// Builds an OData context URL for the given base URL and entity type.
    /// </summary>
    public static string BuildContextUrl(string baseUrl, string entityType, bool isSingle = false)
    {
        var suffix = isSingle ? "/$entity" : "";
        return $"{baseUrl}/odata/$metadata#{entityType}{suffix}";
    }

    /// <summary>
    /// Creates a standardized OData response with proper context and metadata.
    /// </summary>
    public static ODataResponse CreateODataResponse(
        string baseUrl,
        string entityType,
        object[] value,
        long? totalCount = null,
        string? nextLink = null)
    {
        return new ODataResponse
        {
            Context = BuildContextUrl(baseUrl, entityType),
            Count = totalCount,
            NextLink = nextLink,
            Value = value
        };
    }

    /// <summary>
    /// Creates a standardized OData feature response for single feature operations.
    /// </summary>
    public static ODataFeatureResponse CreateODataFeatureResponse(
        string baseUrl,
        long objectId,
        int layerId,
        string? geometry,
        string attributes)
    {
        return new ODataFeatureResponse
        {
            Context = BuildContextUrl(baseUrl, "Features", isSingle: true),
            ObjectId = objectId,
            LayerId = layerId,
            Geometry = geometry,
            Attributes = attributes
        };
    }

    /// <summary>
    /// Extracts the base URL from an HTTP request.
    /// </summary>
    public static string GetBaseUrl(HttpRequest request)
    {
        return BaseUrlResolver.GetBaseUrl(request);
    }

    /// <summary>
    /// Gets the OData content type for responses.
    /// </summary>
    public static string GetODataContentType()
    {
        return ODataContentType;
    }

    /// <summary>
    /// Checks if pagination should be applied based on result size and current parameters.
    /// </summary>
    public static bool ShouldPaginate(int resultCount, int currentSkip, int totalCount, int? top)
    {
        var currentTop = top ?? 1000; // Default page size
        return currentSkip + resultCount < totalCount;
    }

    /// <summary>
    /// Calculates the next skip value for pagination.
    /// </summary>
    public static int CalculateNextSkip(int currentSkip, int? top)
    {
        var currentTop = top ?? 1000;
        return currentSkip + currentTop;
    }

    /// <summary>
    /// Creates location header value for created resources.
    /// </summary>
    public static string CreateLocationHeader(string baseUrl, int layerId, long objectId)
    {
        return $"{baseUrl}/odata/Features({layerId},{objectId})";
    }

    /// <summary>
    /// Creates OData-EntityId header value for created resources.
    /// </summary>
    public static string CreateODataEntityId(string baseUrl, int layerId, long objectId)
    {
        return $"{baseUrl}/odata/Features({layerId},{objectId})";
    }

    /// <summary>
    /// Validates that a layer ID is positive.
    /// </summary>
    public static bool IsValidLayerId(int layerId)
    {
        return layerId > 0;
    }

    /// <summary>
    /// Validates that an object ID is positive.
    /// </summary>
    public static bool IsValidObjectId(long objectId)
    {
        return objectId > 0;
    }

    /// <summary>
    /// Gets the proper HTTP status code for an OData CRUD result.
    /// </summary>
    public static int GetStatusCodeForCrudResult<T>(ODataCrudResult<T> result)
    {
        return result.StatusCode;
    }

    /// <summary>
    /// Creates an IResult from an OData CRUD result with proper headers.
    /// </summary>
    public static IResult CreateResultFromCrudResult<T>(
        HttpContext context,
        ODataCrudResult<T> crudResult)
    {
        if (!crudResult.IsSuccess)
        {
            var code = MapODataCode(crudResult.StatusCode);
            return CreateODataError(context, code, crudResult.ErrorMessage ?? "An error occurred", crudResult.StatusCode);
        }

        SetODataHeaders(context, crudResult.ETag);

        if (crudResult.LocationHeader != null)
        {
            context.Response.Headers.Location = crudResult.LocationHeader;
            context.Response.Headers["OData-EntityId"] = crudResult.LocationHeader;
        }

        if (crudResult.StatusCode == 204)
        {
            return Results.NoContent();
        }

        return Results.Json(
            crudResult.Data,
            GetJsonTypeInfo<T>(),
            contentType: ODataContentType,
            statusCode: crudResult.StatusCode);
    }

    /// <summary>
    /// Gets the appropriate JsonTypeInfo for serialization based on type.
    /// </summary>
    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> GetJsonTypeInfo<T>()
    {
        // This would need to be expanded based on the actual types used
        // For now, return a generic JsonTypeInfo
        return (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)(object)ODataJsonContext.Default.ODataFeatureResponse;
    }

    private static string MapODataCode(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "BadRequest",
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "ResourceNotFound",
        StatusCodes.Status408RequestTimeout => "RequestTimeout",
        StatusCodes.Status413PayloadTooLarge => "PayloadTooLarge",
        StatusCodes.Status429TooManyRequests => "TooManyRequests",
        StatusCodes.Status500InternalServerError => "InternalServerError",
        StatusCodes.Status503ServiceUnavailable => "ServiceUnavailable",
        _ => "Error"
    };

    /// <summary>
    /// Determines if an HTTP method supports a request body.
    /// </summary>
    public static bool MethodSupportsBody(string method)
    {
        return method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
               method.Equals("PUT", StringComparison.OrdinalIgnoreCase) ||
               method.Equals("PATCH", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates an OData feature request for required fields.
    /// </summary>
    public static (bool isValid, string? errorMessage) ValidateFeatureRequest(ODataFeatureRequest? request, string method)
    {
        if (!MethodSupportsBody(method))
        {
            return (true, null); // No body validation needed for GET/DELETE
        }

        if (request == null)
        {
            return (false, "Request body is required for this operation.");
        }

        // Additional validation could be added here based on layer schema
        return (true, null);
    }
}
